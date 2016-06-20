// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net.Security;
using System.Net.Sockets;
using System.Net.Test.Common;
using System.Net.Tests;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Xunit;

namespace System.Net.Http.Functional.Tests
{
    public class HttpClientHandler_ClientCertificates_Test
    {
        [Fact]
        public void ClientCertificateOptions_Default()
        {
            using (var handler = new HttpClientHandler())
            {
                Assert.Equal(ClientCertificateOption.Manual, handler.ClientCertificateOptions);
            }
        }

        [Theory]
        [InlineData((ClientCertificateOption)2)]
        [InlineData((ClientCertificateOption)(-1))]
        public void ClientCertificateOptions_InvalidArg_ThrowsException(ClientCertificateOption option)
        {
            using (var handler = new HttpClientHandler())
            {
                Assert.Throws<ArgumentOutOfRangeException>("value", () => handler.ClientCertificateOptions = option);
            }
        }

        [Theory]
        [InlineData(ClientCertificateOption.Automatic)]
        [InlineData(ClientCertificateOption.Manual)]
        public void ClientCertificateOptions_ValueArg_Roundtrips(ClientCertificateOption option)
        {
            using (var handler = new HttpClientHandler())
            {
                handler.ClientCertificateOptions = option;
                Assert.Equal(option, handler.ClientCertificateOptions);
            }
        }

        [ConditionalFact(nameof(BackendDoesNotSupportCustomCertificateHandling))]
        public async Task Automatic_SSLBackendNotSupported_ThrowsPlatformNotSupportedException()
        {
            using (var client = new HttpClient(new HttpClientHandler() { ClientCertificateOptions = ClientCertificateOption.Automatic }))
            {
                await Assert.ThrowsAsync<PlatformNotSupportedException>(() => client.GetAsync(HttpTestServers.SecureRemoteEchoServer));
            }
        }

        [ConditionalFact(nameof(BackendDoesNotSupportCustomCertificateHandling))]
        public async Task Manual_SSLBackendNotSupported_ThrowsPlatformNotSupportedException()
        {
            var handler = new HttpClientHandler();
            handler.ClientCertificates.Add(CertificateConfiguration.GetClientCertificate());
            using (var client = new HttpClient(handler))
            {
                await Assert.ThrowsAsync<PlatformNotSupportedException>(() => client.GetAsync(HttpTestServers.SecureRemoteEchoServer));
            }
        }

        [ActiveIssue(9543, PlatformID.Windows)]
        [ConditionalTheory(nameof(BackendSupportsCustomCertificateHandling))]
        [InlineData(6, false)] // merge back into Manual_CertificateSentMatchesCertificateReceived_Success once active issue fixed
        public Task Manual_CertificateSentMatchesCertificateReceived_NonReuse_Success(int numberOfRequests,
            bool reuseClient)
        {
            return Manual_CertificateSentMatchesCertificateReceived_Success(numberOfRequests, reuseClient);
        }

        [ConditionalTheory(nameof(BackendSupportsCustomCertificateHandling))]
        [InlineData(3, true)]
        public async Task Manual_CertificateSentMatchesCertificateReceived_Success(
            int numberOfRequests,
            bool reuseClient) // validate behavior with and without connection pooling, which impacts client cert usage
        {
            var options = new LoopbackServer.Options { UseSsl = true };
            using (var cert = CertificateConfiguration.GetClientCertificate())
            {
                Func<HttpClient> createClient = () =>
                {
                    var handler = new HttpClientHandler() { ServerCertificateCustomValidationCallback = delegate { return true; } };
                    handler.ClientCertificates.Add(cert);
                    return new HttpClient(handler);
                };

                Func<HttpClient, Socket, Uri, Task> makeAndValidateRequest = async (client, server, url) =>
                {
                    await TestHelper.WhenAllCompletedOrAnyFailed(
                        client.GetStringAsync(url),
                        LoopbackServer.AcceptSocketAsync(server, async (socket, stream, reader, writer) =>
                        {
                            SslStream sslStream = Assert.IsType<SslStream>(stream);
                            Assert.Equal(cert, sslStream.RemoteCertificate);
                            await LoopbackServer.ReadWriteAcceptedAsync(socket, reader, writer);
                        }, options));
                };

                await LoopbackServer.CreateServerAsync(async (server, url) =>
                {
                    if (reuseClient)
                    {
                        using (var client = createClient())
                        {
                            for (int i = 0; i < numberOfRequests; i++)
                            {
                                await makeAndValidateRequest(client, server, url);

                                GC.Collect();
                                GC.WaitForPendingFinalizers();
                            }
                        }
                    }
                    else
                    {
                        for (int i = 0; i < numberOfRequests; i++)
                        {
                            using (var client = createClient())
                            {
                                await makeAndValidateRequest(client, server, url);
                            }

                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                        }
                    }
                }, options);
            }
        }

        private static bool BackendSupportsCustomCertificateHandling =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            (CurlSslVersionDescription()?.StartsWith("OpenSSL") ?? false);

        private static bool BackendDoesNotSupportCustomCertificateHandling => !BackendSupportsCustomCertificateHandling;

        [DllImport("System.Net.Http.Native", EntryPoint = "HttpNative_GetSslVersionDescription")]
        private static extern string CurlSslVersionDescription();
    }
}