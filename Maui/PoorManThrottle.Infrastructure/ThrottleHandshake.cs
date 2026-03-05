using PoorManThrottle.Core.Abstractions;
using PoorManThrottle.Core.Helpers;
using PoorManThrottle.Core.Models;

namespace PoorManThrottle.Infrastructure;

public sealed class ThrottleHandshake : IThrottleHandshake
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public async Task<string> ActivateAsync(IBleDeviceSession session, CancellationToken cancellationToken = default)
    {
        if (session.ConnectionState != BleConnectionState.Connected)
            throw new InvalidOperationException($"Not connected (state={session.ConnectionState}).");

        // Step 1: Send I, wait for I:<efuse> or ERR:<msg>
        var efuse = await SendAndWaitAsync(
            session,
            commandToSend: "I",
            accept: line =>
            {
                if (TryParseErr(line, out var err)) return HandshakeResult.Fail(err);
                if (TryParseEfuse(line, out var e)) return HandshakeResult.Ok(e);
                return HandshakeResult.Ignore();
            },
            timeout: DefaultTimeout,
            cancellationToken);

        // Step 2: send I,<obfuscated>, wait I:Connected or ERR
        var obf = Obfuscator.Obfuscate12(efuse);
        _ = await SendAndWaitAsync(
            session,
            commandToSend: $"I,{obf}",
            accept: line =>
            {
                if (TryParseErr(line, out var err)) return HandshakeResult.Fail(err);
                if (string.Equals(line?.Trim(), "I:CONNECTED", StringComparison.OrdinalIgnoreCase))
                    return HandshakeResult.Ok("Connected");
                return HandshakeResult.Ignore();
            },
            timeout: DefaultTimeout,
            cancellationToken);

        return efuse;
    }

    private static async Task<string> SendAndWaitAsync(
        IBleDeviceSession session,
        string commandToSend,
        Func<string, HandshakeResult> accept,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            var res = accept(line);

            if (res.Kind == HandshakeResultKind.Ignore)
                return;

            if (res.Kind == HandshakeResultKind.Ok)
                tcs.TrySetResult(res.Value);

            if (res.Kind == HandshakeResultKind.Fail)
                tcs.TrySetException(new InvalidOperationException(res.Value));
        }

        session.LineReceived += Handler;

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await session.SendAsync(commandToSend, cancellationToken);

            // Wait for either:
            // - Ok
            // - ERR -> exception
            // - timeout/cancel
            using var reg = linked.Token.Register(() => tcs.TrySetCanceled(linked.Token));
            return await tcs.Task.ConfigureAwait(false);

        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"Handshake timed out waiting for response to '{commandToSend}'.");
        }
        finally
        {
            session.LineReceived -= Handler;
        }
    }

    private static bool TryParseEfuse(string line, out string efuseHex)
    {
        efuseHex = "";
        var s = line.Trim();

        // Expect: I:<12hex>
        if (!s.StartsWith("I:", StringComparison.OrdinalIgnoreCase))
            return false;

        var payload = s.Substring(2).Trim();
        if (payload.Length != 12)
            return false;

        // hex check
        for (int i = 0; i < payload.Length; i++)
        {
            var c = payload[i];
            var isHex =
                (c >= '0' && c <= '9') ||
                (c >= 'a' && c <= 'f') ||
                (c >= 'A' && c <= 'F');
            if (!isHex) return false;
        }

        efuseHex = payload;
        return true;
    }

    private static bool TryParseErr(string line, out string message)
    {
        message = "";
        var s = line.Trim();
        if (!s.StartsWith("ERR:", StringComparison.OrdinalIgnoreCase))
            return false;

        message = s; // keep full "ERR:..." so UI shows exactly what firmware said
        return true;
    }

    private enum HandshakeResultKind { Ignore, Ok, Fail }

    private readonly record struct HandshakeResult(HandshakeResultKind Kind, string Value)
    {
        public static HandshakeResult Ignore() => new(HandshakeResultKind.Ignore, "");
        public static HandshakeResult Ok(string value) => new(HandshakeResultKind.Ok, value);
        public static HandshakeResult Fail(string value) => new(HandshakeResultKind.Fail, value);
    }
}