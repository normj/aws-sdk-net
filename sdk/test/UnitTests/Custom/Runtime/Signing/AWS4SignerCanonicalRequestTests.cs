/*
 * Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
 *
 * Licensed under the Apache License, Version 2.0 (the "License").
 * You may not use this file except in compliance with the License.
 * A copy of the License is located at
 *
 *  http://aws.amazon.com/apache2.0
 *
 * or in the "license" file accompanying this file. This file is distributed
 * on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either
 * express or implied. See the License for the specific language governing
 * permissions and limitations under the License.
 */

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;
using Amazon.Runtime.Internal.Util;
using Amazon.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AWSSDK.UnitTests.Signing
{
    /// <summary>
    /// The signer only materializes the canonical request string when metrics are enabled. These tests
    /// check that the metrics-enabled and metrics-disabled paths produce identical signatures, and that
    /// the recorded canonical request is the one that was signed.
    /// </summary>
    [TestClass]
    [TestCategory("Core")]
    [TestCategory("Signer")]
    public class AWS4SignerCanonicalRequestTests
    {
        private const string AccessKey = "AKIDEXAMPLE";
        private const string SecretKey = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
        private const string Region = "us-east-1";
        private const string Service = "service";
        private static readonly DateTime SignedAt = new DateTime(2015, 8, 30, 12, 36, 0, DateTimeKind.Utc);

        [TestMethod]
        [DataRow("plain-value")]
        [DataRow("multi-byte \u00e9\u20ac\U0001F600 value")]
        public void SignRequest_MetricsEnabledAndDisabled_ProduceSameSignature(string headerValue)
        {
            var config = new MockClientConfig { AuthenticationRegion = Region };
            var enabled = new RequestMetrics { IsEnabled = true };

            var withMetrics = new AWS4Signer().SignRequest(BuildRequest(headerValue), config, enabled, AccessKey, SecretKey, SignedAt);
            var withDisabledMetrics = new AWS4Signer().SignRequest(BuildRequest(headerValue), config, new RequestMetrics(), AccessKey, SecretKey, SignedAt);
            var withNullMetrics = new AWS4Signer().SignRequest(BuildRequest(headerValue), config, null, AccessKey, SecretKey, SignedAt);

            Assert.AreEqual(withMetrics.ForAuthorizationHeader, withDisabledMetrics.ForAuthorizationHeader);
            Assert.AreEqual(withMetrics.ForAuthorizationHeader, withNullMetrics.ForAuthorizationHeader);
            AssertRecordedCanonicalRequestWasSigned(enabled);
        }

        [TestMethod]
        public void PresignRequest_MetricsEnabledAndDisabled_ProduceSameSignature()
        {
            var config = new MockClientConfig { AuthenticationRegion = Region };
            var enabled = new RequestMetrics { IsEnabled = true };

            var withMetrics = AWS4PreSignedUrlSigner.SignRequest(BuildRequest("v"), config, enabled, AccessKey, SecretKey, Service, Region, SignedAt);
            var withDisabledMetrics = AWS4PreSignedUrlSigner.SignRequest(BuildRequest("v"), config, new RequestMetrics(), AccessKey, SecretKey, Service, Region, SignedAt);
            var withNullMetrics = AWS4PreSignedUrlSigner.SignRequest(BuildRequest("v"), config, null, AccessKey, SecretKey, Service, Region, SignedAt);

            Assert.AreEqual(withMetrics.ForQueryParameters, withDisabledMetrics.ForQueryParameters);
            Assert.AreEqual(withMetrics.ForQueryParameters, withNullMetrics.ForQueryParameters);
            AssertRecordedCanonicalRequestWasSigned(enabled);
        }

        [TestMethod]
        public void SignRequest_MetricsDisabled_MatchesIndependentSignature()
        {
            // Rebuild the signature from the public helpers, which take the canonical request as a string.
            var config = new MockClientConfig { AuthenticationRegion = Region };
            var request = BuildRequest("multi-byte \u00e9 value");
            var result = new AWS4Signer().SignRequest(request, config, new RequestMetrics(), AccessKey, SecretKey, SignedAt);

            var sortedHeaders = AWS4Signer.SortAndPruneHeaders(request.Headers);
            var canonicalRequest = AWS4Signer.CanonicalizeRequest(request.Endpoint, request.ResourcePath, request.HttpMethod,
                sortedHeaders, "Action=ListUsers&Version=2010-05-08", request.Headers[HeaderKeys.XAmzContentSha256Header],
                request.PathResources, request.UseDoubleEncoding);
            var expected = AWS4Signer.ComputeSignature(AccessKey, SecretKey, Region, SignedAt, Service, result.SignedHeaders, canonicalRequest);

            Assert.AreEqual(expected.ForAuthorizationHeader, result.ForAuthorizationHeader);
        }

        private static void AssertRecordedCanonicalRequestWasSigned(RequestMetrics metrics)
        {
            var canonicalRequest = (string)metrics.Properties[Metric.CanonicalRequest].Single();
            var stringToSign = metrics.Properties[Metric.StringToSign].Single().ToString();
            using (var sha256 = SHA256.Create())
            {
                var hash = AWSSDKUtils.ToHex(sha256.ComputeHash(Encoding.UTF8.GetBytes(canonicalRequest)), true);
                Assert.IsTrue(stringToSign.EndsWith("\n" + hash, StringComparison.Ordinal), stringToSign);
            }
        }

        private static IRequest BuildRequest(string headerValue)
        {
            var request = new DefaultRequest(new StubRequest(), Service)
            {
                HttpMethod = "GET",
                Endpoint = new Uri("https://example.amazonaws.com"),
                ResourcePath = "/",
                OverrideSigningServiceName = Service,
                AuthenticationRegion = Region,
                UseQueryString = true,
            };
            request.Parameters["Action"] = "ListUsers";
            request.Parameters["Version"] = "2010-05-08";
            request.Headers["X-Amz-Meta-Test"] = headerValue;
            return request;
        }

        private class StubRequest : AmazonWebServiceRequest { }
    }
}
