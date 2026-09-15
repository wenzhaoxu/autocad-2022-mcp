using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Autodesk.AutoCAD.ApplicationServices.Core;

namespace AutoCAD2024Mcp
{
    internal sealed class PendingRequest
    {
        public JObject Request { get; set; }
        public TaskCompletionSource<JObject> Completion { get; } =
            new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class PipeBridge : IDisposable
    {
        private const int MaxRequestsPerIdle = 4;
        private readonly CadInstanceRegistration _registration;
        private readonly ConcurrentQueue<PendingRequest> _queue = new ConcurrentQueue<PendingRequest>();
        private CancellationTokenSource _cts;
        private Task _listener;
        private int _draining;
        private int _activeRequest;

        public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;
        public string PipeName => _registration.PipeName;

        public PipeBridge() => _registration = new CadInstanceRegistration();

        public void Start()
        {
            if (IsRunning) return;
            _cts = new CancellationTokenSource();
            _listener = Task.Run(() => ListenLoop(_cts.Token));
            UpdateRegistration();
        }

        public void Stop()
        {
            _cts?.Cancel();
            _cts = null;
            _registration.Dispose();
        }

        private async Task ListenLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                    await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                    NamedPipeServerStream connected = pipe;
                    pipe = null;
                    _ = HandleConnectedClient(connected, token);
                }
                catch (OperationCanceledException) { }
                catch
                {
                    if (!token.IsCancellationRequested)
                        await Task.Delay(250, token).ConfigureAwait(false);
                }
                finally { pipe?.Dispose(); }
            }
        }

        private async Task HandleConnectedClient(NamedPipeServerStream pipe, CancellationToken token)
        {
            using (pipe)
            {
                try { await HandleClient(pipe, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch { }
            }
        }

        private async Task HandleClient(Stream pipe, CancellationToken token)
        {
            using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 8192, true))
            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 8192, true) { AutoFlush = true })
            {
                string line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line)) return;
                JObject request;
                try { request = JObject.Parse(line); }
                catch (Exception ex)
                {
                    await writer.WriteLineAsync(JsonConvert.SerializeObject(Error(null, "INVALID_JSON", ex.Message))).ConfigureAwait(false);
                    return;
                }

                var pending = new PendingRequest { Request = request };
                _queue.Enqueue(pending);
                using (token.Register(() => pending.Completion.TrySetCanceled()))
                {
                    JObject response = await pending.Completion.Task.ConfigureAwait(false);
                    await writer.WriteLineAsync(response.ToString(Formatting.None)).ConfigureAwait(false);
                }
            }
        }

        public void DrainOnCadThread()
        {
            UpdateRegistration();
            if (Interlocked.CompareExchange(ref _activeRequest, 0, 0) != 0) return;
            if (Interlocked.Exchange(ref _draining, 1) != 0) return;
            try
            {
                for (int processed = 0; processed < MaxRequestsPerIdle; processed++)
                {
                    if (Interlocked.CompareExchange(ref _activeRequest, 1, 0) != 0) return;
                    if (!_queue.TryDequeue(out PendingRequest pending))
                    {
                        Interlocked.Exchange(ref _activeRequest, 0);
                        return;
                    }
                    string id = (string)pending.Request["id"];
                    string method = (string)pending.Request["method"];
                    Task<JToken> task;
                    try { task = CadDispatcher.DispatchAsync(method, pending.Request["params"] as JObject ?? new JObject()); }
                    catch (Exception ex) { Complete(pending, id, null, ex); continue; }
                    if (task.IsCompleted) CompleteTask(pending, id, task);
                    else
                    {
                        task.ContinueWith(t => CompleteTask(pending, id, t), TaskScheduler.Default);
                        return;
                    }
                }
            }
            finally { Interlocked.Exchange(ref _draining, 0); }
        }

        private void CompleteTask(PendingRequest pending, string id, Task<JToken> task)
        {
            if (task.IsCanceled) Complete(pending, id, null, new CadMcpException("CANCELLED", "AutoCAD operation was cancelled."));
            else if (task.IsFaulted) Complete(pending, id, null, task.Exception?.GetBaseException());
            else Complete(pending, id, task.Result, null);
        }

        private void Complete(PendingRequest pending, string id, JToken result, Exception exception)
        {
            try
            {
                if (exception == null) pending.Completion.TrySetResult(new JObject { ["id"] = id, ["ok"] = true, ["result"] = result });
                else if (exception is CadMcpException known) pending.Completion.TrySetResult(Error(id, known.Code, known.Message, known.Details));
                else pending.Completion.TrySetResult(Error(id, "CAD_EXCEPTION", exception.Message, exception.ToString()));
            }
            finally { Interlocked.Exchange(ref _activeRequest, 0); }
        }

        private void UpdateRegistration()
        {
            try
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                _registration.Write(doc?.Name, doc?.Database?.Filename);
            }
            catch { }
        }

        private static JObject Error(string id, string code, string message, object details = null) => new JObject
        {
            ["id"] = id,
            ["ok"] = false,
            ["error"] = new JObject { ["code"] = code, ["message"] = message, ["details"] = details == null ? null : JToken.FromObject(details) }
        };

        public void Dispose() { Stop(); _registration.Dispose(); }
    }

    internal sealed class CadMcpException : Exception
    {
        public string Code { get; }
        public object Details { get; }
        public CadMcpException(string code, string message, object details = null) : base(message) { Code = code; Details = details; }
    }
}
