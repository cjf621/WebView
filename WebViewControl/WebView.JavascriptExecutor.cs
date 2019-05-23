﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CefSharp;

namespace WebViewControl {

    partial class WebView {

        [DataContract]
        internal class JsError {
            [DataMember(Name = "stack")]
            public string Stack;
            [DataMember(Name = "name")]
            public string Name;
            [DataMember(Name = "message")]
            public string Message;
        }

        internal class ScriptTask {

            public ScriptTask(string script, string functionName, TimeSpan? timeout = default(TimeSpan?), bool awaitable = false) {
                Script = script;
                if (awaitable) {
                    WaitHandle = new ManualResetEvent(false);
                }
                Timeout = timeout;

                // we store the function name apart from the script and use it later in the exception details 
                // this prevents any params to be shown in the message because they can contain sensitive information
                FunctionName = functionName;
            }

            public string Script { get; private set; }

            public string FunctionName { get; private set; }

            public ManualResetEvent WaitHandle { get; private set; }

            public JavascriptResponse Result { get; set; }

            public Exception Exception { get; set; }

            public TimeSpan? Timeout { get; set; }
        }

        internal class JavascriptExecutor : IDisposable {

            private static readonly Regex StackFrameRegex = new Regex(@"at\s*(?<method>.*?)\s\(?(?<location>[^\s]+):(?<line>\d+):(?<column>\d+)", RegexOptions.Compiled);
            private const string InternalException = "|WebViewInternalException";

            private readonly WebView OwnerWebView;
            private readonly BlockingCollection<ScriptTask> pendingScripts = new BlockingCollection<ScriptTask>();
            private readonly CancellationTokenSource flushTaskCancelationToken = new CancellationTokenSource();
            private readonly ManualResetEvent stoppedFlushHandle = new ManualResetEvent(false);

            private IFrame frame;
            private volatile bool flushRunning;

            public JavascriptExecutor(WebView owner, IFrame frame = null) {
                OwnerWebView = owner;
                this.frame = frame;
                OwnerWebView.JavascriptContextCreated += OnJavascriptContextCreated;
            }

            private void OnJavascriptContextCreated(long frameId) {
                if (frame == null) {
                    frame = OwnerWebView.chromium.GetMainFrame();
                }

                if (frameId != frame.Identifier) {
                    return;
                }

                OwnerWebView.JavascriptContextCreated -= OnJavascriptContextCreated;
                OwnerWebView.JavascriptContextReleased += OnJavascriptContextReleased;
                OwnerWebView.RenderProcessCrashed += StopFlush;

                Task.Factory.StartNew(FlushScripts, flushTaskCancelationToken.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }

            private void OnJavascriptContextReleased(long frameId) {
                if (frameId != frame.Identifier) {
                    return;
                }
                StopFlush();
            }

            private void StopFlush() {
                OwnerWebView.JavascriptContextCreated -= OnJavascriptContextCreated;
                OwnerWebView.JavascriptContextReleased -= OnJavascriptContextReleased;
                OwnerWebView.RenderProcessCrashed -= StopFlush;

                if (flushTaskCancelationToken.IsCancellationRequested) {
                    return;
                }
                flushTaskCancelationToken.Cancel();
                if (flushRunning) {
                    stoppedFlushHandle.WaitOne();
                }

                // signal any pending js evaluations
                foreach (var pendingScript in pendingScripts.ToArray()) {
                    pendingScript.WaitHandle?.Set();
                }

                pendingScripts.Dispose();
                flushTaskCancelationToken.Dispose();
            }

            private ScriptTask QueueScript(string script, string functionName = null, TimeSpan? timeout = default(TimeSpan?), bool awaitable = false) {
                if (OwnerWebView.isDisposing) {
                    return null;
                }
                var scriptTask = new ScriptTask(script, functionName, timeout, awaitable);
                pendingScripts.Add(scriptTask);
                return scriptTask;
            }

            private void FlushScripts() {
                OwnerWebView.ExecuteWithAsyncErrorHandling(() => {
                    try {
                        flushRunning = true;
                        while (!flushTaskCancelationToken.IsCancellationRequested) {
                            InnerFlushScripts();
                        }
                    } catch (OperationCanceledException) {
                        // stop
                    } finally {
                        flushRunning = false;
                        stoppedFlushHandle.Set();
                    }
                });
            }

            private void InnerFlushScripts() {
                ScriptTask scriptToEvaluate = null;
                var scriptsToExecute = new List<ScriptTask>();

                do {
                    var scriptTask = pendingScripts.Take(flushTaskCancelationToken.Token);
                    if (scriptTask.WaitHandle == null) {
                        scriptsToExecute.Add(scriptTask);
                    } else { 
                        scriptToEvaluate = scriptTask;
                        break; // this script result needs to be handled separately
                    }
                } while (pendingScripts.Count > 0);

                if (scriptsToExecute.Count > 0) {
                    var script = string.Join(";" + Environment.NewLine, scriptsToExecute.Select(s => s.Script));
                    var task = frame.EvaluateScriptAsync(
                        WrapScriptWithErrorHandling(script), 
                        timeout: OwnerWebView.DefaultScriptsExecutionTimeout);
                    task.Wait(flushTaskCancelationToken.Token);
                    var response = task.Result;
                    if (!response.Success) {
                        var evaluatedScriptFunctions = scriptsToExecute.Select(s => s.FunctionName);
                        OwnerWebView.ExecuteWithAsyncErrorHandling(() => throw ParseResponseException(response, evaluatedScriptFunctions));
                    }
                }

                if (scriptToEvaluate != null) {
                    // evaluate and signal waiting thread
                    Task<JavascriptResponse> task = null;
                    var script = scriptToEvaluate.Script;
                    var timeout = scriptToEvaluate.Timeout ?? OwnerWebView.DefaultScriptsExecutionTimeout;
                    try {
                        task = frame.EvaluateScriptAsync(script, timeout: timeout);
                        task.Wait(flushTaskCancelationToken.Token);
                        scriptToEvaluate.Result = task.Result;
                    } catch(Exception e) {
                        if (task?.IsCanceled == true) {
                            // timeout
                            scriptToEvaluate.Exception = new JavascriptException("Timeout", (timeout.HasValue ? $"More than {timeout.Value.TotalMilliseconds}ms elapsed" : "Timeout ocurred") + $" evaluating the script: '{script}'");
                        } else {
                            scriptToEvaluate.Exception = e;
                        }
                    } finally {
                        scriptToEvaluate.WaitHandle.Set();
                    }
                }
            }

            public T EvaluateScript<T>(string script, string functionName = null, TimeSpan? timeout = default(TimeSpan?)) {
                var scriptWithErrorHandling = WrapScriptWithErrorHandling(script);

                var scriptTask = QueueScript(scriptWithErrorHandling, functionName, timeout, true);
                if (scriptTask == null) {
                    return GetResult<T>(null); // webview is disposing
                }

                if (!flushRunning) {
                    var succeeded = scriptTask.WaitHandle.WaitOne(timeout ?? TimeSpan.FromSeconds(15)); // wait with timeout if flush is not running yet to avoid hanging forever
                    if (!succeeded) {
                        throw new JavascriptException("Timeout", "Javascript engine is not initialized");
                    }
                } else {
                    scriptTask.WaitHandle.WaitOne();
                }

                if (scriptTask.Exception != null) {
                    throw scriptTask.Exception;
                }

                if (scriptTask.Result == null) {
                    return GetResult<T>(null); // webview is disposing
                }

                if (scriptTask.Result.Success) {
                    return GetResult<T>(scriptTask.Result.Result);
                }
                
                throw ParseResponseException(scriptTask.Result, new[] { functionName });
            }

            public T EvaluateScriptFunction<T>(string functionName, bool serializeParams, params object[] args) {
                return EvaluateScript<T>(MakeScript(functionName, serializeParams, args), functionName);
            }

            public void ExecuteScriptFunction(string functionName, bool serializeParams, params object[] args) {
                QueueScript(MakeScript(functionName, serializeParams, args), functionName);
            }

            public void ExecuteScript(string script) {
                QueueScript(script);
            }

            private T GetResult<T>(object result) {
                var targetType = typeof(T);
                if (IsBasicType(targetType)) {
                    if (result == null) {
                        return default(T);
                    }
                    return (T)result;
                }
                if (result == null && targetType.IsArray) {
                    // return empty arrays when value is null and return type is array
                    return (T)(object)Array.CreateInstance(targetType.GetElementType(), 0);
                }
                return (T)OwnerWebView.binder.Bind(result, targetType);
            }

            public void Dispose() {
                StopFlush();
            }

            private static bool IsBasicType(Type type) {
                return type.IsPrimitive || type.IsEnum || type == typeof(string);
            }

            private static string MakeScript(string functionName, bool serializeParams, object[] args) {
                string SerializeParam(object value) {
                    if (serializeParams || value == null) {
                        return JavascriptSerializer.Serialize(value);
                    }
                    // TODO complex types
                    return value.ToString();
                }
                var argsSerialized = args.Select(SerializeParam);
                return functionName + "(" + string.Join(",", argsSerialized) + ")";
            }

            private static string WrapScriptWithErrorHandling(string script) {
                return "try {" + script + Environment.NewLine + "} catch (e) { throw JSON.stringify({ stack: e.stack, message: e.message, name: e.name }) + '" + InternalException + "' }";
            }

            private static T DeserializeJSON<T>(string json) {
                var serializer = new DataContractJsonSerializer(typeof(JsError));
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json))) {
                    return (T)serializer.ReadObject(stream);
                }
            }

            private static Exception ParseResponseException(JavascriptResponse response, IEnumerable<string> evaluatedScriptFunctions) {
                var jsErrorJSON = response.Message;

                // try parse js exception
                jsErrorJSON = jsErrorJSON.Substring(Math.Max(0, jsErrorJSON.IndexOf("{")));
                jsErrorJSON = jsErrorJSON.Substring(0, jsErrorJSON.LastIndexOf("}") + 1);

                var evaluatedStackFrames = evaluatedScriptFunctions.Where(f => !string.IsNullOrEmpty(f))
                                                                   .Select(f => new JavascriptStackFrame() { FunctionName = f, SourceName = "eval" });

                if (!string.IsNullOrEmpty(jsErrorJSON)) {
                    JsError jsError = null;
                    try {
                        jsError = DeserializeJSON<JsError>(jsErrorJSON);
                    } catch {
                        // ignore will throw error at the end   
                    }
                    if (jsError != null) {
                        jsError.Name = jsError.Name ?? "";
                        jsError.Message = jsError.Message ?? "";
                        jsError.Stack = jsError.Stack ?? "";
                        var jsStack = jsError.Stack.Substring(Math.Min(jsError.Stack.Length, (jsError.Name + ": " + jsError.Message).Length))
                                                   .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

                        var parsedStack = new List<JavascriptStackFrame>();

                        parsedStack.AddRange(evaluatedStackFrames);

                        foreach(var stackFrame in jsStack) {
                            var frameParts = StackFrameRegex.Match(stackFrame);
                            if (frameParts.Success) {
                                parsedStack.Add(new JavascriptStackFrame() {
                                    FunctionName = frameParts.Groups["method"].Value,
                                    SourceName = frameParts.Groups["location"].Value,
                                    LineNumber = int.Parse(frameParts.Groups["line"].Value),
                                    ColumnNumber = int.Parse(frameParts.Groups["column"].Value)
                                });
                            }
                        }
                        
                        return new JavascriptException(jsError.Name, jsError.Message, parsedStack);
                    }
                }

                return new JavascriptException("Javascript Error", response.Message, evaluatedStackFrames);
            }

            internal static bool IsInternalException(string exceptionMessage) {
                return exceptionMessage.EndsWith(InternalException);
            }
        }
    }
}