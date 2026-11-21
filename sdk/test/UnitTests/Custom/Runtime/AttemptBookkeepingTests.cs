using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Globalization;
using System.Threading;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins the per-attempt bookkeeping values: the amz-sdk-request header text and the per-endpoint
    /// clock skew correction round trip.
    /// </summary>
    [TestClass]
    [TestCategory("UnitTest")]
    [TestCategory("Runtime")]
    public class AttemptBookkeepingTests
    {
        [TestMethod]
        public void SdkRequestHeaderValue_MatchesFormatForAllCounts()
        {
            var savedCulture = Thread.CurrentThread.CurrentCulture;
            try
            {
                foreach (var culture in new[] { CultureInfo.InvariantCulture, new CultureInfo("ar-SA"), new CultureInfo("fa-IR"), new CultureInfo("de-DE") })
                {
                    Thread.CurrentThread.CurrentCulture = culture;
                    for (int attempt = 0; attempt <= 20; attempt++)
                    {
                        for (int max = 0; max <= 20; max++)
                        {
                            var expected = "attempt=" + attempt.ToString(culture) + "; max=" + max.ToString(culture);
                            Assert.AreEqual(expected, RetryHandler.GetSdkRequestHeaderValue(attempt, max), culture.Name);
                            Assert.AreEqual(expected, RetryHandler.GetSdkRequestHeaderValue(attempt, max), culture.Name);
                        }
                    }
                }

                Assert.AreEqual("attempt=1; max=2147483647", RetryHandler.GetSdkRequestHeaderValue(1, int.MaxValue));
                Assert.AreEqual("attempt=-2147483648; max=1", RetryHandler.GetSdkRequestHeaderValue(int.MinValue, 1));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = savedCulture;
            }
        }

        [TestMethod]
        public void SdkRequestHeader_IsSetOnTheRequestForEachAttempt()
        {
            var config = new AmazonS3ConfigLike();
            var request = new DefaultRequest(new MockAmazonWebServiceRequestLike(), "TestService");
            var requestContext = new RequestContext(false) { ClientConfig = config, Request = request, OriginalRequest = request.OriginalRequest };
            var handler = new RetryHandler(new StandardRetryPolicy(3)) { InnerHandler = new NoOpHandler() };
            handler.Logger = Amazon.Runtime.Internal.Util.Logger.EmptyLogger;

            handler.InvokeSync(new Amazon.Runtime.Internal.ExecutionContext(requestContext, new ResponseContext()));

            Assert.AreEqual("attempt=1; max=4", request.Headers["amz-sdk-request"]);
            Assert.IsTrue(Guid.TryParse(request.Headers["amz-sdk-invocation-id"], out _));
        }

        [TestMethod]
        public void ClockCorrection_RoundTripsExactTicks()
        {
            var endpoint = "https://bookkeeping-test-" + Guid.NewGuid().ToString("N") + ".amazonaws.com/";
            Assert.AreEqual(TimeSpan.Zero, CorrectClockSkew.GetClockCorrectionForEndpoint(endpoint));

            foreach (var correction in new[] { TimeSpan.FromTicks(1), TimeSpan.FromTicks(-12345678901), TimeSpan.FromMinutes(-7.5), TimeSpan.MaxValue, TimeSpan.MinValue, TimeSpan.Zero })
            {
                CorrectClockSkew.SetClockCorrectionForEndpoint(endpoint, correction);
                Assert.AreEqual(correction, CorrectClockSkew.GetClockCorrectionForEndpoint(endpoint));
            }
        }

        [TestMethod]
        public void ClockCorrection_IsAppliedToCorrectedUtcNow()
        {
            var endpoint = "https://bookkeeping-test-" + Guid.NewGuid().ToString("N") + ".amazonaws.com/";
            var savedCorrect = AWSConfigs.CorrectForClockSkew;
            try
            {
                AWSConfigs.CorrectForClockSkew = true;
                CorrectClockSkew.SetClockCorrectionForEndpoint(endpoint, TimeSpan.FromHours(2));
                var corrected = CorrectClockSkew.GetCorrectedUtcNowForEndpoint(endpoint);
                var delta = corrected - DateTime.UtcNow;
                Assert.IsTrue(delta > TimeSpan.FromMinutes(119) && delta < TimeSpan.FromMinutes(121), delta.ToString());
            }
            finally
            {
                AWSConfigs.CorrectForClockSkew = savedCorrect;
                CorrectClockSkew.SetClockCorrectionForEndpoint(endpoint, TimeSpan.Zero);
            }
        }

        private sealed class NoOpHandler : PipelineHandler
        {
            public override void InvokeSync(IExecutionContext executionContext) { }
        }

        private sealed class MockAmazonWebServiceRequestLike : AmazonWebServiceRequest { }

        private sealed class AmazonS3ConfigLike : MockClientConfig { }
    }
}
