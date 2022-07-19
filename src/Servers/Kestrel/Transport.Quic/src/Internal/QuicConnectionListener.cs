// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Quic;
using System.Net.Security;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Quic.Internal;

/// <summary>
/// Listens for new Quic Connections.
/// </summary>
internal sealed class QuicConnectionListener : IMultiplexedConnectionListener, IAsyncDisposable
{
    private readonly ILogger _log;
    private readonly TlsConnectionOptions _tlsConnectionOptions;
    private readonly QuicTransportContext _context;
    private readonly QuicListenerOptions _quicListenerOptions;
    private bool _disposed;
    private QuicListener? _listener;

    public QuicConnectionListener(
        QuicTransportOptions options,
        ILogger log,
        EndPoint endpoint,
        TlsConnectionOptions tlsConnectionOptions)
    {
        if (!QuicListener.IsSupported)
        {
            throw new NotSupportedException("QUIC is not supported or enabled on this platform. See https://aka.ms/aspnet/kestrel/http3reqs for details.");
        }

        if (endpoint is not IPEndPoint listenEndPoint)
        {
            throw new InvalidOperationException($"QUIC doesn't support listening on the configured endpoint type. Expected {nameof(IPEndPoint)} but got {endpoint.GetType().Name}.");
        }

        if (tlsConnectionOptions.ApplicationProtocols.Count == 0)
        {
            throw new InvalidOperationException("No application protocols specified.");
        }

        _log = log;
        _tlsConnectionOptions = tlsConnectionOptions;
        _context = new QuicTransportContext(_log, options);
        _quicListenerOptions = new QuicListenerOptions
        {
            ApplicationProtocols = _tlsConnectionOptions.ApplicationProtocols,
            ListenEndPoint = listenEndPoint,
            ListenBacklog = options.Backlog,
            ConnectionOptionsCallback = async (connection, helloInfo, cancellationToken) =>
            {
                var serverAuthenticationOptions = await _tlsConnectionOptions.OnConnection(new TlsConnectionCallbackContext
                {
                    CancellationToken = cancellationToken,
                    ClientHelloInfo = helloInfo,
                    State = _tlsConnectionOptions.OnConnectionState,
                    Connection = null!,
                });

                // If the callback didn't set protocols then use the listener's list of protocols.
                if (serverAuthenticationOptions.ApplicationProtocols == null)
                {
                    serverAuthenticationOptions.ApplicationProtocols = _tlsConnectionOptions.ApplicationProtocols;
                }

                // If the SslServerAuthenticationOptions doesn't have a cert or protocols then the
                // QUIC connection will fail and the client receives an unhelpful message.
                // Validate the options on the server and log issues to improve debugging.
                ValidateServerAuthenticationOptions(serverAuthenticationOptions);

                var connectionOptions = new QuicServerConnectionOptions
                {
                    ServerAuthenticationOptions = serverAuthenticationOptions,
                    IdleTimeout = options.IdleTimeout,
                    MaxInboundBidirectionalStreams = options.MaxBidirectionalStreamCount,
                    MaxInboundUnidirectionalStreams = options.MaxUnidirectionalStreamCount,
                    DefaultCloseErrorCode = 0,
                    DefaultStreamErrorCode = 0,
                };
                return connectionOptions;
            }
        };

        // Setting to listenEndPoint to prevent the property from being null.
        // This will be initialized when CreateListenerAsync() is invoked.
        EndPoint = listenEndPoint;
    }

    private void ValidateServerAuthenticationOptions(SslServerAuthenticationOptions serverAuthenticationOptions)
    {
        if (serverAuthenticationOptions.ServerCertificate == null &&
            serverAuthenticationOptions.ServerCertificateContext == null &&
            serverAuthenticationOptions.ServerCertificateSelectionCallback == null)
        {
            QuicLog.ConnectionListenerCertificateNotSpecified(_log);
        }
        if (serverAuthenticationOptions.ApplicationProtocols == null || serverAuthenticationOptions.ApplicationProtocols.Count == 0)
        {
            QuicLog.ConnectionListenerApplicationProtocolsNotSpecified(_log);
        }
    }

    public EndPoint EndPoint { get; set; }

    public async ValueTask CreateListenerAsync()
    {
        _listener = await QuicListener.ListenAsync(_quicListenerOptions);

        // Listener endpoint will resolve an ephemeral port, e.g. 127.0.0.1:0, into the actual port.
        EndPoint = _listener.LocalEndPoint;
    }

    public async ValueTask<MultiplexedConnectionContext?> AcceptAsync(IFeatureCollection? features = null, CancellationToken cancellationToken = default)
    {
        if (_listener == null)
        {
            throw new InvalidOperationException($"The listener needs to be initialized by calling {nameof(CreateListenerAsync)}.");
        }

        try
        {
            var quicConnection = await _listener.AcceptConnectionAsync(cancellationToken);
            var connectionContext = new QuicConnectionContext(quicConnection, _context);

            QuicLog.AcceptedConnection(_log, connectionContext);

            return connectionContext;
        }
        catch (QuicException ex) when (ex.QuicError == QuicError.OperationAborted)
        {
            _log.LogDebug("Listener has aborted with exception: {Message}", ex.Message);
        }
        return null;
    }

    public async ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_listener != null)
        {
            await _listener.DisposeAsync();
        }
        _disposed = true;
    }
}
