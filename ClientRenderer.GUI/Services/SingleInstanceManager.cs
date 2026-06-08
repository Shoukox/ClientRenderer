using Avalonia.Threading;
using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClientRenderer.GUI.Services
{
    public sealed class SingleInstanceManager : IDisposable
    {
        private readonly string _mutexName;
        private readonly string _pipeName;
        private readonly Mutex _mutex;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly object _sync = new();
        private bool _disposed;
        private bool _hasPendingActivation;
        private Action? _activationHandler;
        private Task? _listenerTask;

        public SingleInstanceManager(string appId)
        {
            if (string.IsNullOrWhiteSpace(appId))
                throw new ArgumentException("Application id cannot be null or empty.", nameof(appId));

            string instanceKey = createInstanceKey(appId);
            _mutexName = $"Local\\{instanceKey}";
            _pipeName = instanceKey;
            _mutex = new Mutex(true, _mutexName, out bool createdNew);
            IsPrimaryInstance = createdNew;
        }

        public bool IsPrimaryInstance { get; }

        public void StartListening()
        {
            if (!IsPrimaryInstance)
                throw new InvalidOperationException("Only the primary instance can listen for activation requests.");

            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _listenerTask ??= Task.Run(() => listenLoopAsync(_shutdown.Token));
            }
        }

        public void RegisterActivationHandler(Action handler)
        {
            ArgumentNullException.ThrowIfNull(handler);

            bool invokeImmediately;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _activationHandler = handler;
                invokeImmediately = _hasPendingActivation;
                _hasPendingActivation = false;
            }

            if (invokeImmediately)
                dispatchActivation(handler);
        }

        [SupportedOSPlatform("windows")]
        public bool SignalPrimaryInstance(TimeSpan timeout)
        {
            using NamedPipeClientStream client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.None);
            client.Connect((int)Math.Max(1, timeout.TotalMilliseconds));

            using StreamWriter writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true);
            writer.WriteLine("SHOW");
            writer.Flush();
            client.WaitForPipeDrain();
            return true;
        }

        private async Task listenLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using NamedPipeServerStream server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Message,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                    using StreamReader reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                    string? command = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

                    if (string.Equals(command, "SHOW", StringComparison.Ordinal))
                        requestActivation();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private void requestActivation()
        {
            Action? handler;
            lock (_sync)
            {
                handler = _activationHandler;
                if (handler == null)
                {
                    _hasPendingActivation = true;
                    return;
                }
            }

            dispatchActivation(handler);
        }

        private static void dispatchActivation(Action handler)
        {
            Dispatcher.UIThread.Post(handler, DispatcherPriority.Send);
        }

        private static string createInstanceKey(string appId)
        {
            string userScope = Environment.UserDomainName + "\\" + Environment.UserName;
            string raw = appId + "|" + userScope;
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return "ClientRenderer_" + Convert.ToHexString(hash);
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
            }

            _shutdown.Cancel();

            try
            {
                _listenerTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Best-effort shutdown.
            }

            _shutdown.Dispose();

            if (IsPrimaryInstance)
                _mutex.ReleaseMutex();

            _mutex.Dispose();
        }
    }
}
