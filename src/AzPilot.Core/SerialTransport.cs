using System.IO.Ports;
using System.Text;

namespace AzPilot.Core;

public sealed class SerialTransport : IDisposable
{
    private SerialPort? _port;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    public bool IsOpen => _port?.IsOpen == true;
    public static string[] GetPortNames() => SerialPort.GetPortNames().OrderBy(x => x).ToArray();

    public void Open(string portName, int baudRate)
    {
        Close();
        if (string.IsNullOrWhiteSpace(portName)) throw new MountException("Select a serial port before connecting.");
        var port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            Encoding = Encoding.ASCII,
            ReadTimeout = 800,
            WriteTimeout = 800,
            Handshake = Handshake.None,
            DtrEnable = false,
            RtsEnable = false,
            NewLine = "\r"
        };
        try { port.Open(); port.DiscardInBuffer(); port.DiscardOutBuffer(); _port = port; }
        catch (Exception ex) { port.Dispose(); throw new MountException($"Could not open {portName}. Close SynScan and other mount software, then try again.", ex); }
    }

    public async Task<string> QueryAsync(string command, CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken);
        try
        {
            if (!IsOpen) throw new MountException("The serial connection is not open.");
            var port = _port!;
            port.DiscardInBuffer();
            await port.BaseStream.WriteAsync(Encoding.ASCII.GetBytes(command), cancellationToken);
            await port.BaseStream.FlushAsync(cancellationToken);
            var response = new StringBuilder();
            var one = new byte[1];
            var deadline = DateTime.UtcNow.AddMilliseconds(1100);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (port.BytesToRead == 0) { await Task.Delay(8, cancellationToken); continue; }
                var read = await port.BaseStream.ReadAsync(one, cancellationToken);
                if (read == 0) continue;
                var character = (char)one[0];
                if (response.Length == 0 && character is not ('=' or '!')) continue;
                response.Append(character);
                if (character == '\r') break;
                if (response.Length > 64) throw new MountException("The mount returned an overlong response.");
            }
            if (response.Length == 0 || response[^1] != '\r')
                throw new TimeoutException($"No complete reply to {command.Trim()} within 1.1 seconds.");
            var text = response.ToString();
            if (text[0] == '!') throw new MountException($"Mount rejected {command.Trim()}: {text.Trim()}");
            return text.Substring(1, text.Length - 2);
        }
        catch (MountException) { throw; }
        catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException or UnauthorizedAccessException)
        { throw new MountException("USB communication failed. Motion should be checked at the mount.", ex); }
        finally { _ioLock.Release(); }
    }

    public void Close()
    {
        try { if (_port?.IsOpen == true) _port.Close(); } catch { }
        _port?.Dispose(); _port = null;
    }
    public void Dispose() { Close(); _ioLock.Dispose(); }
}
