#if !NETFRAMEWORK
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;
using Amazon.Runtime.Internal.Util;
using Amazon.Runtime.Logging;
using AWSSDK_DotNet.UnitTests;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins how HttpHandler copies the request headers onto the HttpRequestMessage and logs the User-Agent.
    /// </summary>
    [TestClass]
    [TestCategory("UnitTest")]
    [TestCategory("Runtime")]
    public class HttpRequestMessageHeadersTests
    {
        private sealed class CapturingHandler : HttpMessageHandler
        {
            public HttpRequestMessage LastRequest { get; private set; }
            public List<KeyValuePair<string, string>> LastHeaders { get; private set; }

            protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) => Capture(request);

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Capture(request));

            private HttpResponseMessage Capture(HttpRequestMessage request)
            {
                LastRequest = request;
                LastHeaders = request.Headers.Select(h => new KeyValuePair<string, string>(h.Key, string.Join(",", h.Value))).ToList();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[0]), RequestMessage = request };
            }
        }

        private sealed class CapturingFactory : HttpClientFactory
        {
            public CapturingHandler Handler { get; } = new CapturingHandler();

            public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new HttpClient(Handler);
        }

        private sealed class RecordingLogger : ILogger
        {
            public List<string> DebugMessages { get; } = new List<string>();

            public void Flush() { }
            public void Error(Exception exception, string messageFormat, params object[] args) { }
            public void Debug(Exception exception, string messageFormat, params object[] args) { }
            public void DebugFormat(string messageFormat, params object[] args) => DebugMessages.Add(string.Format(messageFormat, args));
            public void InfoFormat(string messageFormat, params object[] args) { }
        }

        private sealed class RecordingAdaptorFactory : IAdaptorLoggerFactory, IAdaptorLogger
        {
            public string Name => "HttpRequestMessageHeadersTests";
            public bool DebugEnabled { get; set; }
            public List<string> DebugMessages { get; } = new List<string>();

            public IAdaptorLogger CreateAdaptorLogger(Type type) => this;
            public bool IsEnabled(SdkLogLevel level) => level != SdkLogLevel.Debug || DebugEnabled;

            public void Log(SdkLogLevel level, string message, Exception ex, params object[] parameters)
            {
                if (level == SdkLogLevel.Debug)
                    DebugMessages.Add(string.Format(message, parameters));
            }
        }

        private static (HttpHandler<HttpContent> handler, CapturingFactory factory) CreateHandler()
        {
            var factory = new CapturingFactory();
            var config = new MockClientConfig { HttpClientFactory = factory };
            var handler = new HttpHandler<HttpContent>(new HttpRequestMessageFactory(config), new object());
            return (handler, factory);
        }

        private static IExecutionContext CreateContext(IClientConfig config, Action<IRequest> configure)
        {
            var original = new MockAmazonWebServiceRequest();
            var request = new DefaultRequest(original, "TestService")
            {
                HttpMethod = "GET",
                Endpoint = new Uri("https://example.amazonaws.com"),
                ResourcePath = "/",
            };
            configure?.Invoke(request);
            var requestContext = new RequestContext(false, new NullSigner())
            {
                ClientConfig = config,
                OriginalRequest = original,
                Request = request,
            };
            return new Amazon.Runtime.Internal.ExecutionContext(requestContext, new ResponseContext());
        }

        private static MockClientConfig ConfigFor(CapturingFactory factory) => new MockClientConfig { HttpClientFactory = factory };

        [TestMethod]
        public void RequestHeaders_AreCopiedInOrder_AndContentHeadersAreLeftOut()
        {
            var (handler, factory) = CreateHandler();
            handler.Logger = Logger.EmptyLogger;
            var context = CreateContext(ConfigFor(factory), r =>
            {
                r.Headers["X-Amz-Target"] = "Service.Operation";
                r.Headers["content-type"] = "application/x-amz-json-1.0";
                r.Headers["amz-sdk-invocation-id"] = "c2b5f9f0-6c36-4c8b-9a3b-0e4f9f1c6a11";
                r.Headers["Content-MD5"] = "1B2M2Y8AsgTpgAmY7PhCfg==";
                r.Headers["X-Custom"] = "a, b";
                r.Headers["expires"] = "Thu, 01 Dec 1994 16:00:00 GMT";
            });

            handler.InvokeSync(context);

            var sent = factory.Handler.LastHeaders;
            CollectionAssert.AreEqual(
                new[] { "X-Amz-Target", "amz-sdk-invocation-id", "X-Custom", "User-Agent" },
                sent.Select(h => h.Key).ToArray());
            Assert.AreEqual("Service.Operation", sent[0].Value);
            Assert.AreEqual("a, b", sent[2].Value);
            Assert.AreEqual(context.RequestContext.Request.Headers["User-Agent"], sent[3].Value);
        }

        [TestMethod]
        public void SetRequestHeaders_NonDictionaryHeaders_AreCopiedInTheirOrder()
        {
            var headers = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Z-Last", "z" },
                { "Content-Length", "10" },
                { "A-First", "a" },
                { "Content-Range", "bytes 0-9/10" },
                { "M-Middle", "m" },
            };

            using (var message = new HttpWebRequestMessage(new HttpClient(), new Uri("https://example.com"), new MockClientConfig()))
            {
                message.SetRequestHeaders(headers);

                CollectionAssert.AreEqual(new[] { "A-First", "M-Middle", "Z-Last" }, message.Request.Headers.Select(h => h.Key).ToArray());
            }
        }

        [TestMethod]
        public void SetRequestHeaders_DictionaryHeaders_MatchEnumerationOrderAfterRemovals()
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "H1", "1" }, { "H2", "2" }, { "Content-Type", "text/plain" }, { "H3", "3" },
            };
            headers.Remove("H1");
            headers["H4"] = "4";

            using (var message = new HttpWebRequestMessage(new HttpClient(), new Uri("https://example.com"), new MockClientConfig()))
            {
                message.SetRequestHeaders(headers);

                var expected = headers.Keys.Where(k => k != "Content-Type").ToArray();
                CollectionAssert.AreEqual(expected, message.Request.Headers.Select(h => h.Key).ToArray());
            }
        }

        [TestMethod]
        public void UserAgent_IsLoggedToCustomLogger()
        {
            var (handler, factory) = CreateHandler();
            var logger = new RecordingLogger();
            handler.Logger = logger;
            var context = CreateContext(ConfigFor(factory), null);

            handler.InvokeSync(context);

            var userAgent = context.RequestContext.Request.Headers["User-Agent"];
            CollectionAssert.Contains(logger.DebugMessages, "User-Agent Header: " + userAgent);
        }

        [TestMethod]
        public void UserAgent_IsLoggedOnlyWhenADebugAdaptorIsEnabled()
        {
            var adaptor = new RecordingAdaptorFactory();
            Logger.RegisterAdaptorLoggerFactory(adaptor);
            try
            {
                foreach (var debugEnabled in new[] { false, true })
                {
                    adaptor.DebugEnabled = debugEnabled;
                    adaptor.DebugMessages.Clear();
                    var (handler, factory) = CreateHandler();
                    handler.Logger = Logger.GetLogger(typeof(HttpRequestMessageHeadersTests));
                    var context = CreateContext(ConfigFor(factory), null);

                    handler.InvokeSync(context);

                    var userAgent = context.RequestContext.Request.Headers["User-Agent"];
                    var logged = adaptor.DebugMessages.Contains("User-Agent Header: " + userAgent);
                    Assert.AreEqual(debugEnabled, logged, $"debugEnabled={debugEnabled}");
                }
            }
            finally
            {
                Logger.DeregisterAdaptorLoggerFactory(adaptor.Name);
            }
        }
    }
}
#endif
