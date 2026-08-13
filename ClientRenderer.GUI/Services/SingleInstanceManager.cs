using Avalonia.Threading;
using ClientRenderer.Logging;
using System;
using System.IO;
using System.IO.Pipes;
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

            // "Local\" is a Windows kernel-object-namespace prefix; it has no meaning
            // on Unix, so only apply it when actually running on Windows.
            _mutexName = OperatingSystem.IsWindows() ? $"Local\\{instanceKey}" : instanceKey;
            _pipeName = instanceKey;

            _mutex = new Mutex(true, _mutexName, out bool createdNew);
            IsPrimaryInstance = createdNew;
            Logger.Log($"Single-instance manager initialized. Primary instance: {IsPrimaryInstance}.");
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

            Logger.Log("Single-instance activation listener started.");
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
            {
                Logger.Log("Dispatching pending activation request.");
                dispatchActivation(handler);
            }
        }

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
                    // PipeTransmissionMode.Message is Windows-only; the Unix named-pipe
                    // implementation only supports Byte mode, so use that everywhere.
                    using NamedPipeServerStream server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                    using StreamReader reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                    string? command = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

                    if (string.Equals(command, "SHOW", StringComparison.Ordinal))
                    {
                        Logger.Log("Received activation request from another instance.");
                        requestActivation();
                    }
                    else
                    {
                        Logger.LogWarning($"Received unknown activation command: {command ?? "<empty>"}");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Single-instance activation listener failed. Retrying...");
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
                    Logger.Log("Activation request queued until the handler is registered.");
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

            // Truncate the hash to keep the resulting name short: on Linux, named
            // pipes are backed by a Unix domain socket file, and socket paths have
            // a ~108-byte OS limit (sun_path). A shorter, still-unique name keeps
            // us well clear of that limit even once combined with a temp path.
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            string shortHash = Convert.ToHexString(hash, 0, 8); // 16 hex chars
            return "cr-" + shortHash;
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
            catch (Exception ex)
            {
                Logger.LogWarning($"Single-instance listener did not stop cleanly: {ex.Message}");
            }

            _shutdown.Dispose();

            if (IsPrimaryInstance)
                _mutex.ReleaseMutex();

            _mutex.Dispose();
        }
    }
}