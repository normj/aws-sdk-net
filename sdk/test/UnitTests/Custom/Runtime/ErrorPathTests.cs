using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Util;
using Amazon.Runtime.Logging;
using AWSSDK.Runtime.Internal.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins the error-path helpers used on every failed request: ExceptionUtils.IsInnerException over
    /// plain chains and AggregateException trees, and which loggers receive the retry and error
    /// handler messages.
    /// </summary>
    [TestClass]
    [TestCategory("UnitTest")]
    [TestCategory("Runtime")]
    public class ErrorPathTests
    {
        // Reference implementation: the breadth-first search ExceptionUtils used before the chain fast path.
        private static bool ReferenceIsInnerException<T>(Exception exception, out T inner) where T : Exception
        {
            inner = null;
            var queue = new Queue<Exception>();
            var currentException = exception;
            do
            {
                if (queue.Count > 0)
                {
                    currentException = queue.Dequeue();
                    inner = currentException as T;
                    if (inner != null)
                        return true;
                }
                if (currentException is AggregateException aggregate)
                {
                    foreach (var e in aggregate.InnerExceptions)
                        queue.Enqueue(e);
                    continue;
                }
                if (currentException.InnerException != null)
                    queue.Enqueue(currentException.InnerException);
            }
            while (queue.Count > 0);
            return false;
        }

        private static IEnumerable<Exception> Shapes()
        {
            var io = new IOException("io");
            yield return new IOException("root only");
            yield return new AmazonServiceException("service");
            yield return new AmazonServiceException("wrap", io);
            yield return new HttpRequestException("h", new WebException("w", new IOException("deep")));
            yield return new InvalidOperationException("a", new InvalidOperationException("b", new InvalidOperationException("c")));
            yield return new AggregateException(new IOException("x"), new WebException("y"));
            yield return new AggregateException(new InvalidOperationException("p", new WebException("q")), new IOException("r"));
            yield return new AmazonServiceException("s", new AggregateException(new InvalidOperationException("t"), new ObjectDisposedException("u")));
            yield return new InvalidOperationException("v", new AggregateException(new AggregateException(new WebException("z"))));
            yield return new AggregateException();
            yield return new WebException("first", new WebException("second"));
        }

        private static void AssertSame<T>(Exception root) where T : Exception
        {
            var expected = ReferenceIsInnerException<T>(root, out var expectedInner);
            var actual = ExceptionUtils.IsInnerException<T>(root, out var actualInner);
            Assert.AreEqual(expected, actual, $"{typeof(T).Name} in {root}");
            Assert.AreSame(expectedInner, actualInner, $"{typeof(T).Name} in {root}");
            Assert.AreEqual(expected, ExceptionUtils.IsInnerException<T>(root));
        }

        [TestMethod]
        public void IsInnerException_MatchesBreadthFirstSearch()
        {
            foreach (var root in Shapes())
            {
                AssertSame<IOException>(root);
                AssertSame<WebException>(root);
                AssertSame<ObjectDisposedException>(root);
                AssertSame<InvalidOperationException>(root);
                AssertSame<AggregateException>(root);
                AssertSame<Exception>(root);
            }
        }

        [TestMethod]
        public void IsInnerException_DoesNotMatchTheRoot()
        {
            Assert.IsFalse(ExceptionUtils.IsInnerException<IOException>(new IOException("root")));
            Assert.IsTrue(ExceptionUtils.IsInnerException<IOException>(new IOException("root", new IOException("inner")), out var inner));
            Assert.AreEqual("inner", inner.Message);
        }

        [TestMethod]
        public void IsInnerException_NullRoot_ThrowsNullReference()
        {
            Assert.ThrowsExactly<NullReferenceException>(() => ExceptionUtils.IsInnerException<IOException>(null));
        }

        private sealed class RecordingLogger : ILogger
        {
            public List<string> Messages { get; } = new List<string>();
            public void InfoFormat(string messageFormat, params object[] args) => Messages.Add("info:" + string.Format(messageFormat, args));
            public void Debug(Exception exception, string messageFormat, params object[] args) => Messages.Add("debug:" + string.Format(messageFormat, args));
            public void DebugFormat(string messageFormat, params object[] args) => Messages.Add("debug:" + string.Format(messageFormat, args));
            public void Error(Exception exception, string messageFormat, params object[] args) => Messages.Add("error:" + string.Format(messageFormat, args));
        }

        private sealed class RecordingAdaptor : IAdaptorLoggerFactory, IAdaptorLogger
        {
            public string Name => "ErrorPathTests";
            public HashSet<SdkLogLevel> Enabled { get; } = new HashSet<SdkLogLevel>();
            public List<string> Messages { get; } = new List<string>();
            public IAdaptorLogger CreateAdaptorLogger(Type type) => this;
            public bool IsEnabled(SdkLogLevel level) => Enabled.Contains(level);
            public void Log(SdkLogLevel level, string message, Exception ex, params object[] parameters)
                => Messages.Add(level + ":" + string.Format(message, parameters));
        }

        private sealed class FailingHandler : PipelineHandler
        {
            public override void InvokeSync(IExecutionContext executionContext)
                => throw new AmazonServiceException("bad request", ErrorType.Sender, "ValidationException", "REQ", HttpStatusCode.BadRequest);
        }

        private static IExecutionContext CreateContext()
        {
            var original = new MockAmazonWebServiceRequest();
            var request = new DefaultRequest(original, "TestService") { Endpoint = new Uri("https://example.amazonaws.com") };
            var requestContext = new RequestContext(false) { ClientConfig = new MockClientConfig(), OriginalRequest = original, Request = request };
            return new Amazon.Runtime.Internal.ExecutionContext(requestContext, new ResponseContext());
        }

        private static void InvokeFailingPipeline(ILogger logger)
        {
            var errorHandler = new ErrorHandler(logger) { InnerHandler = new FailingHandler() };
            var retryHandler = new RetryHandler(new StandardRetryPolicy(2)) { InnerHandler = errorHandler };
            retryHandler.Logger = logger;
            Assert.ThrowsExactly<AmazonServiceException>(() => retryHandler.InvokeSync(CreateContext()));
        }

        [TestMethod]
        public void CustomLogger_ReceivesRetryAndErrorHandlerMessages()
        {
            var logger = new RecordingLogger();

            InvokeFailingPipeline(logger);

            CollectionAssert.AreEqual(new[]
            {
                "error:An exception of type AmazonServiceException was handled in ErrorHandler.",
                "debug:Retry check: IsStaleConnectionError=False, CanRetry=True, staleConnectionRetries=0, maxStaleConnectionRetries=" + new MockClientConfig().MaxStaleConnectionRetries + ", IsRequestStreamRewindable=True",
                "error:AmazonServiceException making request MockAmazonWebServiceRequest to https://example.amazonaws.com/. Attempt 1.",
            }, logger.Messages);
        }

        [TestMethod]
        public void SdkLogger_LogsOnlyEnabledLevels()
        {
            var adaptor = new RecordingAdaptor();
            Logger.RegisterAdaptorLoggerFactory(adaptor);
            try
            {
                var logger = Logger.GetLogger(typeof(ErrorPathTests));

                InvokeFailingPipeline(logger);
                Assert.AreEqual(0, adaptor.Messages.Count);

                adaptor.Enabled.Add(SdkLogLevel.Error);
                InvokeFailingPipeline(logger);
                CollectionAssert.AreEqual(new[]
                {
                    "Error:An exception of type AmazonServiceException was handled in ErrorHandler.",
                    "Error:AmazonServiceException making request MockAmazonWebServiceRequest to https://example.amazonaws.com/. Attempt 1.",
                }, adaptor.Messages);

                adaptor.Messages.Clear();
                adaptor.Enabled.Add(SdkLogLevel.Debug);
                InvokeFailingPipeline(logger);
                Assert.AreEqual(3, adaptor.Messages.Count);
                StringAssert.StartsWith(adaptor.Messages[1], "Debug:Retry check: IsStaleConnectionError=False");
            }
            finally
            {
                Logger.DeregisterAdaptorLoggerFactory(adaptor.Name);
            }
        }
    }
}
