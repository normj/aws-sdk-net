using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Telemetry;
using Amazon.Runtime.Telemetry.Metrics;
using Amazon.Runtime.Telemetry.Metrics.NoOp;
using Amazon.Runtime.Telemetry.Tracing;
using Amazon.Runtime.Telemetry.Tracing.NoOp;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins the strings the telemetry helpers pass to a provider to what they were before
    /// <see cref="TelemetryNameCache"/> started reusing them.
    /// </summary>
    [TestClass]
    [TestCategory("Core")]
    public class TelemetryNameCacheTests
    {
        private static readonly string[] ServiceIds = { null, "", "S3", "DynamoDB", "Elastic Beanstalk", "s3" };

        private static readonly string[] RequestNames =
        {
            "PutObjectRequest", "GetItemRequest", "Request", "", "GetItem", "request",
            "PutObjectrequest", "RequestRequest", "ListObjectsV2Request", "Ünïcødé" + "Request",
        };

        [TestMethod]
        public void GetScope_MatchesInterpolatedScope()
        {
            foreach (var serviceId in ServiceIds)
            {
                for (int i = 0; i < 2; i++)
                    Assert.AreEqual($"{TelemetryConstants.TelemetryScopePrefix}.{serviceId}", TelemetryNameCache.GetScope(serviceId), serviceId);
            }
        }

        [TestMethod]
        public void GetScope_IsCaseSensitive()
        {
            Assert.AreEqual("AWSSDK.S3", TelemetryNameCache.GetScope("S3"));
            Assert.AreEqual("AWSSDK.s3", TelemetryNameCache.GetScope("s3"));
        }

        [TestMethod]
        public void GetOperationName_MatchesExtractOperationName()
        {
            foreach (var requestName in RequestNames)
            {
                for (int i = 0; i < 2; i++)
                    Assert.AreEqual(AWSSDKUtils.ExtractOperationName(requestName), TelemetryNameCache.GetOperationName(requestName), requestName);
            }
        }

        [TestMethod]
        public void GetOperationName_NullThrowsAsBefore()
        {
            Assert.ThrowsExactly<NullReferenceException>(() => AWSSDKUtils.ExtractOperationName(null));
            Assert.ThrowsExactly<NullReferenceException>(() => TelemetryNameCache.GetOperationName(null));
        }

        [TestMethod]
        public void GetCallSpanName_MatchesInterpolatedSpanName()
        {
            foreach (var serviceId in ServiceIds)
            {
                foreach (var requestName in RequestNames)
                {
                    var expected = $"{serviceId}.{AWSSDKUtils.ExtractOperationName(requestName)}";
                    for (int i = 0; i < 2; i++)
                        Assert.AreEqual(expected, TelemetryNameCache.GetCallSpanName(serviceId, requestName), $"{serviceId}/{requestName}");
                }
            }
        }

        [TestMethod]
        public void GetCallSpanName_NullRequestNameThrowsAsBefore()
        {
            Assert.ThrowsExactly<NullReferenceException>(() => TelemetryNameCache.GetCallSpanName("S3", null));
        }

        [TestMethod]
        public void GetServerAddress_MatchesInterpolatedAddress()
        {
            var hosts = new[] { null, "", "s3.us-west-2.amazonaws.com", "localhost", "127.0.0.1", "[::1]", "LOCALHOST" };
            var ports = new[] { 0, 80, 443, 8080, 65535, -1 };
            foreach (var host in hosts)
            {
                foreach (var port in ports)
                {
                    for (int i = 0; i < 2; i++)
                        Assert.AreEqual($"{host}:{port.ToString()}", TelemetryNameCache.GetServerAddress(host, port), $"{host}/{port}");
                }
            }
        }

        [TestMethod]
        public void GetServerAddress_NegativePortFollowsCurrentCulture()
        {
            var original = Thread.CurrentThread.CurrentCulture;
            try
            {
                var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
                culture.NumberFormat.NegativeSign = "~";
                Thread.CurrentThread.CurrentCulture = culture;
                Assert.AreEqual("localhost:~1", TelemetryNameCache.GetServerAddress("localhost", -1));

                Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
                Assert.AreEqual("localhost:-1", TelemetryNameCache.GetServerAddress("localhost", -1));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
            }
        }

        [TestMethod]
        public void KeysBeyondTheSizeLimit_StillReturnCorrectValues()
        {
            int count = TelemetryNameCache.MaxEntries + 50;
            for (int i = 0; i < count; i++)
            {
                var serviceId = "LimitService" + i.ToString(CultureInfo.InvariantCulture);
                var requestName = "LimitOperation" + i.ToString(CultureInfo.InvariantCulture) + "Request";
                var host = "limit" + i.ToString(CultureInfo.InvariantCulture) + ".example.com";

                Assert.AreEqual("AWSSDK." + serviceId, TelemetryNameCache.GetScope(serviceId));
                Assert.AreEqual("LimitOperation" + i.ToString(CultureInfo.InvariantCulture), TelemetryNameCache.GetOperationName(requestName));
                Assert.AreEqual(serviceId + ".LimitOperation" + i.ToString(CultureInfo.InvariantCulture), TelemetryNameCache.GetCallSpanName(serviceId, requestName));
                Assert.AreEqual(host + ":443", TelemetryNameCache.GetServerAddress(host, 443));
            }
        }

        [TestMethod]
        public void CreateSpan_PassesScopeAndOperationName()
        {
            var tracerProvider = new CapturingTracerProvider();
            var config = new AmazonS3Config { TelemetryProvider = new DefaultTelemetryProvider() };
            config.TelemetryProvider.RegisterTracerProvider(tracerProvider);
            var requestContext = new RequestContext(false) { ClientConfig = config, OriginalRequest = new PutObjectRequest() };

            for (int i = 0; i < 2; i++)
            {
                TracingUtilities.CreateSpan(requestContext, "span");
                Assert.AreEqual("AWSSDK." + ((IClientConfig)config).ServiceId, tracerProvider.Scopes[i]);
                Assert.AreEqual("PutObject", tracerProvider.Attributes[i].Get(TelemetryConstants.MethodAttributeKey));
                Assert.AreEqual(((IClientConfig)config).ServiceId, tracerProvider.Attributes[i].Get(TelemetryConstants.ServiceAttributeKey));
            }
        }

        [TestMethod]
        public void MeasureDurationAndCounters_PassScopeAndOperationName()
        {
            var meterProvider = new CapturingMeterProvider();
            var config = new AmazonS3Config { TelemetryProvider = new DefaultTelemetryProvider() };
            config.TelemetryProvider.RegisterMeterProvider(meterProvider);
            var requestContext = new RequestContext(false) { ClientConfig = config, OriginalRequest = new GetObjectRequest() };

            using (var measurer = MetricsUtilities.MeasureDuration(requestContext, TelemetryConstants.CallDurationMetricName))
            {
                Assert.IsNotNull(measurer);
            }
            MetricsUtilities.AddMonotonicCounterValue(requestContext, TelemetryConstants.CallAttemptsMetricName, TelemetryConstants.AttemptUnitName);
            MetricsUtilities.MeasureDuration(config, TelemetryConstants.CallDurationMetricName)?.Dispose();

            Assert.AreEqual(3, meterProvider.Scopes.Count);
            foreach (var scope in meterProvider.Scopes)
                Assert.AreEqual("AWSSDK." + ((IClientConfig)config).ServiceId, scope);
        }

        [TestMethod]
        public void CallSpan_UsesServiceMetadataServiceIdAndOperationName()
        {
            var tracerProvider = new CapturingTracerProvider();
            var config = new AmazonS3Config { TelemetryProvider = new DefaultTelemetryProvider() };
            config.TelemetryProvider.RegisterTracerProvider(tracerProvider);

            var spanNames = new List<string>();
            for (int i = 0; i < 2; i++)
            {
                var executionContext = new Amazon.Runtime.Internal.ExecutionContext(
                    new RequestContext(false)
                    {
                        ClientConfig = config,
                        OriginalRequest = new PutObjectRequest(),
                        ServiceMetaData = new TestServiceMetadata("Custom Service"),
                    },
                    new ResponseContext());

                var handler = new MetricsHandler { InnerHandler = new NoOpInnerHandler() };
                handler.InvokeSync(executionContext);
            }

            Assert.AreEqual(2, tracerProvider.SpanNames.Count);
            foreach (var name in tracerProvider.SpanNames)
                Assert.AreEqual("Custom Service.PutObject", name);
        }

        private sealed class TestServiceMetadata : IServiceMetadata
        {
            public TestServiceMetadata(string serviceId) { ServiceId = serviceId; }
            public string ServiceId { get; }
            public IDictionary<string, string> OperationNameMapping => new Dictionary<string, string>();
        }

        private sealed class NoOpInnerHandler : PipelineHandler
        {
            public override void InvokeSync(IExecutionContext executionContext)
                => executionContext.ResponseContext.Response = new PutObjectResponse();
        }

        private sealed class CapturingMeterProvider : MeterProvider
        {
            public List<string> Scopes { get; } = new List<string>();

            public override Meter GetMeter(string scope, Attributes attributes = null)
            {
                Scopes.Add(scope);
                return new NoOpMeter();
            }
        }

        private sealed class CapturingTracerProvider : TracerProvider
        {
            public List<string> Scopes { get; } = new List<string>();
            public List<Attributes> Attributes { get; } = new List<Attributes>();
            public List<string> SpanNames { get; } = new List<string>();

            public override Tracer GetTracer(string scope)
            {
                Scopes.Add(scope);
                return new CapturingTracer(this);
            }

            private sealed class CapturingTracer : Tracer
            {
                private readonly CapturingTracerProvider _owner;
                public CapturingTracer(CapturingTracerProvider owner) { _owner = owner; }

                public override TraceSpan CreateSpan(string name, Attributes initialAttributes = null, SpanKind spanKind = SpanKind.INTERNAL, SpanContext parentContext = null)
                {
                    _owner.SpanNames.Add(name);
                    _owner.Attributes.Add(initialAttributes);
                    return new NoOpTraceSpan();
                }
            }
        }
    }
}
