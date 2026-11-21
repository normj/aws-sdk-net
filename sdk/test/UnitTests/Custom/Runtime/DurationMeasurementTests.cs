using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Telemetry;
using Amazon.Runtime.Telemetry.Metrics;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins the behavior of MetricsUtilities duration measurements: the struct-returning StartDuration used by the
    /// pipeline handlers and the IDisposable-returning MeasureDuration must record the same histogram values with the
    /// same attributes, and record nothing with the default no-op meter provider.
    /// </summary>
    [TestClass]
    [TestCategory("Core")]
    public class DurationMeasurementTests
    {
        private const string MetricName = "client.call.duration";

        [TestMethod]
        public void StartDuration_NoOpProvider_IsInactiveAndRecordsNothing()
        {
            var config = new AmazonS3Config { TelemetryProvider = new DefaultTelemetryProvider() };

            var fromContext = MetricsUtilities.StartDuration(CreateRequestContext(config), MetricName);
            var fromConfig = MetricsUtilities.StartDuration(config, MetricName);

            Assert.IsFalse(fromContext.IsActive);
            Assert.IsFalse(fromConfig.IsActive);
            fromContext.Dispose();
            fromConfig.Dispose();
            default(MetricsUtilities.DurationMeasurement).Dispose();
        }

        [TestMethod]
        public void MeasureDuration_NoOpProvider_ReturnsNull()
        {
            var config = new AmazonS3Config { TelemetryProvider = new DefaultTelemetryProvider() };

            Assert.IsNull(MetricsUtilities.MeasureDuration(CreateRequestContext(config), MetricName));
            Assert.IsNull(MetricsUtilities.MeasureDuration(config, MetricName));
        }

        [TestMethod]
        public void StartDuration_CustomProvider_RecordsOnceWithCommonAttributes()
        {
            var meterProvider = new RecordingMeterProvider();
            var config = CreateConfig(meterProvider);

            using (MetricsUtilities.StartDuration(CreateRequestContext(config), MetricName))
            {
                Assert.AreEqual(1, meterProvider.Meter.CreatedHistograms.Count, "The histogram is created when the measurement starts.");
                Assert.AreEqual(0, meterProvider.Meter.Records.Count);
            }

            Assert.AreEqual("AWSSDK." + ((IClientConfig)config).ServiceId, meterProvider.Scopes.Single());
            Assert.AreEqual(MetricName, meterProvider.Meter.CreatedHistograms[0].Name);
            Assert.AreEqual(TelemetryConstants.SecondsUnitName, meterProvider.Meter.CreatedHistograms[0].Units);

            var record = meterProvider.Meter.Records.Single();
            Assert.IsTrue(record.Value >= 0);
            Assert.AreEqual("PutObject", record.Attributes.Get(TelemetryConstants.MethodAttributeKey));
            Assert.AreEqual(((IClientConfig)config).ServiceId, record.Attributes.Get(TelemetryConstants.ServiceAttributeKey));
            Assert.AreEqual(TelemetryConstants.SystemAttributeValue, record.Attributes.Get(TelemetryConstants.SystemAttributeKey));
        }

        [TestMethod]
        public void StartDuration_CustomProvider_UsesTheCallersAttributes()
        {
            var meterProvider = new RecordingMeterProvider();
            var config = CreateConfig(meterProvider);
            var attributes = new Attributes();
            attributes.Set("custom", "value");

            MetricsUtilities.StartDuration(CreateRequestContext(config), MetricName, attributes).Dispose();

            var record = meterProvider.Meter.Records.Single();
            Assert.AreSame(attributes, record.Attributes);
            Assert.AreEqual("value", record.Attributes.Get("custom"));
            Assert.AreEqual("PutObject", record.Attributes.Get(TelemetryConstants.MethodAttributeKey));
        }

        [TestMethod]
        public void StartDuration_FromConfig_DoesNotAddTheMethodAttribute()
        {
            var meterProvider = new RecordingMeterProvider();
            var config = CreateConfig(meterProvider);

            MetricsUtilities.StartDuration(config, MetricName).Dispose();

            var record = meterProvider.Meter.Records.Single();
            Assert.IsNull(record.Attributes.Get(TelemetryConstants.MethodAttributeKey));
            Assert.AreEqual(((IClientConfig)config).ServiceId, record.Attributes.Get(TelemetryConstants.ServiceAttributeKey));
        }

        [TestMethod]
        public void MeasureDuration_CustomProvider_RecordsOnDispose()
        {
            var meterProvider = new RecordingMeterProvider();
            var config = CreateConfig(meterProvider);

            var measurer = MetricsUtilities.MeasureDuration(CreateRequestContext(config), MetricName);
            Assert.IsNotNull(measurer);
            Assert.AreEqual(0, meterProvider.Meter.Records.Count);
            measurer.Dispose();

            var record = meterProvider.Meter.Records.Single();
            Assert.AreEqual("PutObject", record.Attributes.Get(TelemetryConstants.MethodAttributeKey));

            var clientMeasurer = MetricsUtilities.MeasureDuration(config, "client.uptime");
            Assert.IsNotNull(clientMeasurer);
            clientMeasurer.Dispose();
            Assert.AreEqual(2, meterProvider.Meter.Records.Count);
        }

        [TestMethod]
        public void MeasureDuration_MeterReturningNullHistogram_StillThrowsOnDispose()
        {
            // A custom meter that returns a null histogram made the measurer throw NullReferenceException when it
            // was disposed. Both the class and the struct form keep that.
            var meterProvider = new RecordingMeterProvider { ReturnNullHistogram = true };
            var config = CreateConfig(meterProvider);

            var measurer = MetricsUtilities.MeasureDuration(CreateRequestContext(config), MetricName);
            Assert.IsNotNull(measurer);
            Assert.ThrowsExactly<NullReferenceException>(() => measurer.Dispose());

            var measurement = MetricsUtilities.StartDuration(CreateRequestContext(config), MetricName);
            Assert.IsTrue(measurement.IsActive);
            Assert.ThrowsExactly<NullReferenceException>(() => measurement.Dispose());
        }

        [TestMethod]
        public void StartDuration_ElapsedTimeIsMeasuredFromStart()
        {
            var meterProvider = new RecordingMeterProvider();
            var config = CreateConfig(meterProvider);

            var measurement = MetricsUtilities.StartDuration(CreateRequestContext(config), MetricName);
            System.Threading.Thread.Sleep(20);
            measurement.Dispose();

            Assert.IsTrue(meterProvider.Meter.Records.Single().Value >= 0.015,
                "Recorded " + meterProvider.Meter.Records.Single().Value + " s");
        }

        [TestMethod]
        public async Task StartDuration_InAsyncMethod_RecordsOnce()
        {
            var meterProvider = new RecordingMeterProvider();
            var config = CreateConfig(meterProvider);

            using (MetricsUtilities.StartDuration(CreateRequestContext(config), MetricName))
            {
                await Task.Yield();
            }

            Assert.AreEqual(1, meterProvider.Meter.Records.Count);
        }

        [TestMethod]
        public void StartDuration_UsingRecordsEvenWhenTheBodyThrows()
        {
            var meterProvider = new RecordingMeterProvider();
            var config = CreateConfig(meterProvider);

            Assert.ThrowsExactly<InvalidOperationException>(() =>
            {
                using (MetricsUtilities.StartDuration(CreateRequestContext(config), MetricName))
                {
                    throw new InvalidOperationException();
                }
            });

            Assert.AreEqual(1, meterProvider.Meter.Records.Count);
        }

        private static AmazonS3Config CreateConfig(RecordingMeterProvider meterProvider)
        {
            // Own TelemetryProvider so the process-wide AWSConfigs.TelemetryProvider is never touched.
            var config = new AmazonS3Config { TelemetryProvider = new DefaultTelemetryProvider() };
            config.TelemetryProvider.RegisterMeterProvider(meterProvider);
            return config;
        }

        private static RequestContext CreateRequestContext(AmazonS3Config config)
            => new RequestContext(false) { ClientConfig = config, OriginalRequest = new PutObjectRequest() };

        private sealed class RecordingMeterProvider : MeterProvider
        {
            public bool ReturnNullHistogram { get; set; }
            public List<string> Scopes { get; } = new List<string>();
            public RecordingMeter Meter { get; } = new RecordingMeter();

            public override Meter GetMeter(string scope, Attributes attributes = null)
            {
                Scopes.Add(scope);
                Meter.ReturnNullHistogram = ReturnNullHistogram;
                return Meter;
            }
        }

        private sealed class RecordingMeter : Meter
        {
            public bool ReturnNullHistogram { get; set; }
            public List<(string Name, string Units)> CreatedHistograms { get; } = new List<(string, string)>();
            public List<(double Value, Attributes Attributes)> Records { get; } = new List<(double, Attributes)>();

            public override UpDownCounter<T> CreateUpDownCounter<T>(string name, string units = null, string description = null)
                => throw new NotSupportedException();

            public override MonotonicCounter<T> CreateMonotonicCounter<T>(string name, string units = null, string description = null)
                => throw new NotSupportedException();

            public override Histogram<T> CreateHistogram<T>(string name, string units = null, string description = null)
            {
                CreatedHistograms.Add((name, units));
                if (ReturnNullHistogram)
                    return null;
                return (Histogram<T>)(object)new RecordingHistogram(this);
            }
        }

        private sealed class RecordingHistogram : Histogram<double>
        {
            private readonly RecordingMeter _meter;

            public RecordingHistogram(RecordingMeter meter) => _meter = meter;

            public override void Record(double value, Attributes attributes = null) => _meter.Records.Add((value, attributes));
        }
    }
}
