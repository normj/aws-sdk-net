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
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.Runtime.Internal.Auth;
using Amazon.Runtime.Internal.Util;
using Amazon.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AWSSDK.UnitTests.Signing
{
    /// <summary>
    /// Covers the allocation-free date formatting and string-to-sign encoding used by the SigV4 signer.
    /// </summary>
    [TestClass]
    [TestCategory("Core")]
    [TestCategory("Signer")]
    public class AWS4SignerStringToSignTests
    {
        private static readonly DateTime[] Dates =
        {
            DateTime.MinValue,
            new DateTime(1, 2, 3, 4, 5, 6, DateTimeKind.Utc),
            new DateTime(999, 12, 31, 23, 59, 59, DateTimeKind.Utc),
            new DateTime(2015, 8, 30, 12, 36, 0, DateTimeKind.Utc),
            new DateTime(2024, 2, 29, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 11, 20, 9, 7, 3, 999, DateTimeKind.Utc),
            new DateTime(2026, 11, 20, 9, 7, 3, DateTimeKind.Local),
            new DateTime(2026, 11, 20, 9, 7, 3, DateTimeKind.Unspecified),
            new DateTime(9999, 12, 31, 23, 59, 59, DateTimeKind.Utc),
            DateTime.MaxValue,
        };

        [TestMethod]
        public void FormatDateTime_SigV4Formats_MatchDateTimeToString()
        {
            foreach (var date in Dates)
            {
                foreach (var format in new[] { AWSSDKUtils.ISO8601BasicDateTimeFormat, AWSSDKUtils.ISO8601BasicDateFormat })
                {
                    var expected = date.ToUniversalTime().ToString(format, CultureInfo.InvariantCulture);
                    Assert.AreEqual(expected, AWS4Signer.FormatDateTime(date, format), $"{date:o} {date.Kind} {format}");
                }
            }
        }

        [TestMethod]
        public void FormatDateTime_OtherFormats_Unchanged()
        {
            var date = new DateTime(2026, 11, 20, 9, 7, 3, DateTimeKind.Utc);
            foreach (var format in new[] { "yyyy-MM-ddTHH:mm:ss.fffZ", "yyyyMMdd'T'HHmmss'Z'", "yyyyMMddTHHmmss", "YYYYMMDD" })
                Assert.AreEqual(date.ToString(format, CultureInfo.InvariantCulture), AWS4Signer.FormatDateTime(date, format), format);
        }

        [TestMethod]
        public void InitializeHeaders_SetsXAmzDate()
        {
            var headers = new System.Collections.Generic.Dictionary<string, string>();
            var date = new DateTime(2026, 11, 20, 9, 7, 3, DateTimeKind.Utc);
            AWS4Signer.InitializeHeaders(headers, new Uri("https://example.amazonaws.com"), date);
            Assert.AreEqual("20261120T090703Z", headers[HeaderKeys.XAmzDateHeader]);
        }

        [TestMethod]
        public void ComputeSignature_MatchesIndependentHmac()
        {
            var cases = new[]
            {
                (Region: "us-east-1", Service: "s3"),
                (Region: "r\u00e9gion-\u20ac-\U0001F600", Service: "servi\u00e7e"),
                // Long enough that the UTF-8 string to sign does not fit the stack buffer.
                (Region: new string('\u20ac', 200), Service: "service"),
            };

            foreach (var date in Dates.Where(d => d.Year > 1 && d.Year < 9999))
            {
                foreach (var c in cases)
                {
                    var result = AWS4Signer.ComputeSignature("AKIDEXAMPLE", "secret", c.Region, date, c.Service, "host", "canonical-request");
                    Assert.AreEqual(IndependentSignature("secret", c.Region, c.Service, date, "canonical-request"), result.Signature, $"{date:o} {c.Region}");
                }
            }
        }

        [TestMethod]
        public void ComputeSignature_MetricsEnabledAndDisabled_ProduceSameSignature()
        {
            var date = new DateTime(2026, 11, 20, 9, 7, 3, DateTimeKind.Utc);
            var metrics = new RequestMetrics { IsEnabled = true };
            var withMetrics = AWS4Signer.ComputeSignature("AKIDEXAMPLE", "secret", "us-west-2", date, "s3", "host", "canonical-request", metrics);
            var withoutMetrics = AWS4Signer.ComputeSignature("AKIDEXAMPLE", "secret", "us-west-2", date, "s3", "host", "canonical-request", new RequestMetrics());

            Assert.AreEqual(withMetrics.Signature, withoutMetrics.Signature);
            Assert.AreEqual(ExpectedStringToSign("us-west-2", "s3", date, "canonical-request"), metrics.Properties[Metric.StringToSign].Single().ToString());
        }

        [TestMethod]
        public void ComputeSignature_SecretsThatCollideWhenConcatenated_UseDistinctKeys()
        {
            // The signing key cache is keyed by (secret, region, service). Inputs that are equal when joined with
            // '/' must still each get their own signing key.
            var date = new DateTime(2026, 11, 20, 9, 7, 3, DateTimeKind.Utc);
            var a = AWS4Signer.ComputeSignature("AKIDEXAMPLE", "secret/us-east-1", "s3", date, "x", "host", "canonical-request");
            var b = AWS4Signer.ComputeSignature("AKIDEXAMPLE", "secret", "us-east-1/s3", date, "x", "host", "canonical-request");

            Assert.AreEqual(IndependentSignature("secret/us-east-1", "s3", "x", date, "canonical-request"), a.Signature);
            Assert.AreEqual(IndependentSignature("secret", "us-east-1/s3", "x", date, "canonical-request"), b.Signature);
        }

        private static string ExpectedStringToSign(string region, string service, DateTime signedAt, string canonicalRequest)
        {
            var utc = signedAt.ToUniversalTime();
            using (var sha256 = SHA256.Create())
            {
                return "AWS4-HMAC-SHA256\n" + utc.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture) + "\n" +
                    utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "/" + region + "/" + service + "/aws4_request\n" +
                    AWSSDKUtils.ToHex(sha256.ComputeHash(Encoding.UTF8.GetBytes(canonicalRequest)), true);
            }
        }

        private static string IndependentSignature(string secret, string region, string service, DateTime signedAt, string canonicalRequest)
        {
            var date = signedAt.ToUniversalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var stringToSign = ExpectedStringToSign(region, service, signedAt, canonicalRequest);
            using (var hmac = new HMACSHA256(AWS4Signer.ComposeSigningKey(secret, region, date, service)))
                return AWSSDKUtils.ToHex(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)), true);
        }
    }
}
