using System.Net;
using System.Net.Sockets;

namespace SqlOS.AuthServer.Services;

internal static class SqlOSCimdHttpHandlerFactory
{
    internal static SocketsHttpHandler Create()
        => Create(ResolveAddressesAsync);

    internal static SocketsHttpHandler Create(
        Func<string, CancellationToken, ValueTask<IPAddress[]>> resolveAddresses)
        => new()
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = (context, cancellationToken) =>
                ConnectToPublicAddressAsync(context, resolveAddresses, cancellationToken)
        };

    internal static async ValueTask<Stream> ConnectToPublicAddressAsync(
        SocketsHttpConnectionContext context,
        Func<string, CancellationToken, ValueTask<IPAddress[]>> resolveAddresses,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await resolveAddresses(context.DnsEndPoint.Host, cancellationToken);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            throw new HttpRequestException("CIMD metadata host could not be resolved.", ex);
        }

        if (addresses.Length == 0 || addresses.Any(SqlOSCimdClientService.IsUnsafeAddress))
        {
            throw new HttpRequestException("CIMD metadata host resolved to a non-public address.");
        }

        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                lastError = ex;
                if (ex is OperationCanceledException)
                {
                    throw;
                }
            }
        }

        throw new HttpRequestException("CIMD metadata host could not be reached.", lastError);
    }

    private static async ValueTask<IPAddress[]> ResolveAddressesAsync(
        string host,
        CancellationToken cancellationToken)
        => await Dns.GetHostAddressesAsync(host, cancellationToken);
}
