using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;
using Amazon.Runtime.Telemetry;
using Amazon.Runtime.Telemetry.Tracing;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Model.Internal.MarshallTransformations;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins what MetricsHandler records on the call span when a request fails, and that the failure
    /// itself reaches the caller unchanged with both the default no-op tracer and a real tracer.
    /// </summary>
    [TestClass]
    [TestCategory("UnitTest")]
    [TestCategory("Runtime")]
    public class MetricsHandlerExceptionTests
    {
        private sealed class ThrowingHandler : PipelineHandler
        {
            public Exception Exception { get; set; }

            public override void InvokeSync(IExecutionContext executionContext) => throw Exception;

            public override async Task<T> InvokeAsync<T>(IExecutionContext executionContext)
            {
                await Task.Yield();
                throw Exception;
            }
        }

        private sealed class RecordingTracerProvider : TracerProvider
        {
            public List<RecordingSpan> Spans { get; } = new List<RecordingSpan>();
            public override Tracer GetTracer(string scope) => new RecordingTracer(Spans);

            private sealed class RecordingTracer : Tracer
            {
                private readonly List<RecordingSpan> _spans;
                public RecordingTracer(List<RecordingSpan> spans) { _spans = spans; }

                public override TraceSpan CreateSpan(string name, Attributes initialAttributes = null, SpanKind spanKind = SpanKind.INTERNAL, SpanContext parentContext = null)
                {
                    var span = new RecordingSpan(name);
                    _spans.Add(span);
                    return span;
                }
            }
        }

        private sealed class RecordingSpan : TraceSpan
        {
            public RecordingSpan(string name) { Name = name; }
            public Dictionary<string, object> Attributes { get; } = new Dictionary<string, object>();
            public List<Exception> RecordedExceptions { get; } = new List<Exception>();
            public bool Ended { get; private set; }
            public override void SetAttribute(string key, object value) => Attributes[key] = value;
            public override void EmitEvent(string name, Attributes attributes = null) { }
            public override void SetStatus(SpanStatus status) { }
            public override void RecordException(Exception exception, Attributes attributes = null) => RecordedExceptions.Add(exception);
            public override void End() => Ended = true;
        }

        private static AmazonServiceException CreateServiceException()
        {
            var exception = new AmazonServiceException("The conditional request failed", ErrorType.Sender, "ConditionalCheckFailedException", "REQ123", HttpStatusCode.BadRequest);
            try
            {
                throw exception;
            }
            catch (AmazonServiceException)
            {
                // Thrown once so StackTrace is populated, as it is for a real failure.
            }
            return exception;
        }

        private static IExecutionContext CreateContext(AmazonS3Config config)
        {
            var original = new ListBucketsRequest();
            var context = new Amazon.Runtime.Internal.ExecutionContext(
                new RequestContext(false, new NullSigner())
                {
                    ClientConfig = config,
                    OriginalRequest = original,
                    Request = new ListBucketsRequestMarshaller().Marshall(original),
                    Unmarshaller = new ListBucketsResponseUnmarshaller(),
                },
                new ResponseContext());
            var serviceMetaData = Assembly.GetAssembly(context.GetType()).CreateInstance("Amazon.Runtime.Internal.ServiceMetadata");
            context.RequestContext.GetType().GetProperty("ServiceMetaData").SetValue(context.RequestContext, serviceMetaData);
            return context;
        }

        private static MetricsHandler CreateHandler(Exception exception)
        {
            return new MetricsHandler
            {
                InnerHandler = new ThrowingHandler { Exception = exception },
                Logger = Amazon.Runtime.Internal.Util.Logger.EmptyLogger,
            };
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task NoOpTracer_FailurePropagatesUnchanged(bool async)
        {
            var expected = CreateServiceException();
            var handler = CreateHandler(expected);
            var context = CreateContext(new AmazonS3Config());

            Exception actual = null;
            try
            {
                if (async) await handler.InvokeAsync<AmazonWebServiceResponse>(context);
                else handler.InvokeSync(context);
            }
            catch (Exception e)
            {
                actual = e;
            }

            Assert.AreSame(expected, actual);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task RecordingTracer_CallSpanGetsExceptionDetails(bool async)
        {
            var tracerProvider = new RecordingTracerProvider();
            var config = new AmazonS3Config { TelemetryProvider = new DefaultTelemetryProvider() };
            config.TelemetryProvider.RegisterTracerProvider(tracerProvider);
            var expected = CreateServiceException();
            var handler = CreateHandler(expected);
            var context = CreateContext(config);

            Exception actual = null;
            try
            {
                if (async) await handler.InvokeAsync<AmazonWebServiceResponse>(context);
                else handler.InvokeSync(context);
            }
            catch (Exception e)
            {
                actual = e;
            }

            Assert.AreSame(expected, actual);
            var span = tracerProvider.Spans.Single();
            CollectionAssert.AreEqual(new Exception[] { expected }, span.RecordedExceptions);
            Assert.AreEqual(typeof(AmazonServiceException).ToString(), span.Attributes[TelemetryConstants.ExceptionTypeAttributeKey]);
            Assert.AreEqual(expected.Message, span.Attributes[TelemetryConstants.ExceptionMessageAttributeKey]);
            Assert.IsFalse(string.IsNullOrEmpty((string)span.Attributes[TelemetryConstants.ExceptionStackTraceAttributeKey]));
            Assert.AreEqual("ConditionalCheckFailedException", span.Attributes[TelemetryConstants.AWSErrorCodeAttributeKey]);
            Assert.AreEqual(400, span.Attributes[TelemetryConstants.HTTPStatusCodeAttributeKey]);
            Assert.AreEqual("REQ123", span.Attributes[TelemetryConstants.RequestIdAttributeKey]);
        }
    }
}
