using System;
using System.Globalization;
using System.Text;
using Amazon.Runtime.Internal.Auth;
using Amazon.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins AWS4SigningResult.ForAuthorizationHeader and ForQueryParameters to the strings they were built
    /// from before they were written in one pass.
    /// </summary>
    [TestClass]
    public class AWS4SigningResultStringTests
    {
        private static readonly DateTime SignedAt = new DateTime(2026, 11, 20, 21, 5, 9, DateTimeKind.Utc);

        private static string ReferenceHeader(AWS4SigningResult r)
            => $"{AWS4Signer.AWS4AlgorithmTag} {AWS4Signer.Credential}={r.AccessKeyId}/{r.Scope}, " +
               $"{AWS4Signer.SignedHeaders}={r.SignedHeaders}, {AWS4Signer.Signature}={r.Signature}";

        private static string ReferenceQuery(AWS4SigningResult r)
            => new StringBuilder()
                .AppendFormat("{0}={1}", AWSSDKUtils.UrlEncode(HeaderKeys.XAmzAlgorithm, false), AWSSDKUtils.UrlEncode(AWS4Signer.AWS4AlgorithmTag, false))
                .AppendFormat("&{0}={1}", AWSSDKUtils.UrlEncode(HeaderKeys.XAmzCredential, false), AWSSDKUtils.UrlEncode(string.Format(CultureInfo.InvariantCulture, "{0}/{1}", r.AccessKeyId, r.Scope), false))
                .AppendFormat("&{0}={1}", AWSSDKUtils.UrlEncode(HeaderKeys.XAmzDateHeader, false), AWSSDKUtils.UrlEncode(r.ISO8601DateTime, false))
                .AppendFormat("&{0}={1}", AWSSDKUtils.UrlEncode(HeaderKeys.XAmzSignedHeadersHeader, false), AWSSDKUtils.UrlEncode(r.SignedHeaders, false))
                .AppendFormat("&{0}={1}", AWSSDKUtils.UrlEncode(HeaderKeys.XAmzSignature, false), AWSSDKUtils.UrlEncode(r.Signature, false))
                .ToString();

        private static byte[] Bytes(int length, int seed)
        {
            var bytes = new byte[length];
            new Random(seed).NextBytes(bytes);
            return bytes;
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void StringsMatchReference()
        {
            var accessKeys = new[] { "AKIDEXAMPLE", null, "", "AK/ID+=é\uD800", new string('A', 300) };
            var scopes = new[] { "20261120/us-west-2/s3/aws4_request", null, "", "20261120/région 1/svc😀/aws4_request", "\uDC00tail" };
            var signedHeaders = new[] { "host", "content-type;host;x-amz-content-sha256;x-amz-date;x-amz-security-token", null, "", new string('h', 600) };
            var signatures = new[] { Bytes(32, 1), Bytes(32, 2), new byte[0], new byte[] { 0x00, 0x0f, 0xf0, 0xff, 0xab } };

            foreach (var accessKey in accessKeys)
            foreach (var scope in scopes)
            foreach (var headers in signedHeaders)
            foreach (var signature in signatures)
            {
                var result = new AWS4SigningResult(accessKey, "secret", SignedAt, headers, scope, Bytes(32, 3), signature);
                Assert.AreEqual(ReferenceHeader(result), result.ForAuthorizationHeader);
                Assert.AreEqual(ReferenceQuery(result), result.ForQueryParameters);
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void KnownHeaderValue()
        {
            var signature = new byte[32];
            for (int i = 0; i < signature.Length; i++)
                signature[i] = (byte)(i * 8 + 1);
            var result = new AWS4SigningResult("AKIDEXAMPLE", "secret", SignedAt, "host;x-amz-date", "20261120/us-west-2/s3/aws4_request", new byte[32], signature);

            Assert.AreEqual(
                "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20261120/us-west-2/s3/aws4_request, SignedHeaders=host;x-amz-date, " +
                "Signature=0109111921293139414951596169717981899199a1a9b1b9c1c9d1d9e1e9f1f9",
                result.ForAuthorizationHeader);
            Assert.AreEqual(
                "X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Credential=AKIDEXAMPLE%2F20261120%2Fus-west-2%2Fs3%2Faws4_request" +
                "&X-Amz-Date=20261120T210509Z&X-Amz-SignedHeaders=host%3Bx-amz-date" +
                "&X-Amz-Signature=0109111921293139414951596169717981899199a1a9b1b9c1c9d1d9e1e9f1f9",
                result.ForQueryParameters);
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void SubclassSignatureOverrideIsUsed()
        {
            var result = new OverridingResult("AKIDEXAMPLE", SignedAt, "host", "20261120/us-west-2/s3/aws4_request", Bytes(32, 4));
            StringAssert.EndsWith(result.ForAuthorizationHeader, "Signature=overridden");
            StringAssert.EndsWith(result.ForQueryParameters, "X-Amz-Signature=overridden");
            Assert.AreEqual(ReferenceHeader(result), result.ForAuthorizationHeader);
            Assert.AreEqual(ReferenceQuery(result), result.ForQueryParameters);
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void NullSignatureThrowsAsBefore()
        {
            var result = new AWS4SigningResult("AKIDEXAMPLE", "secret", SignedAt, "host", "scope", new byte[32], null);
            var expected = CaptureException(() => result.Signature);
            Assert.IsNotNull(expected);
            Assert.AreEqual(expected.GetType(), CaptureException(() => result.ForAuthorizationHeader)?.GetType());
            Assert.AreEqual(expected.GetType(), CaptureException(() => result.ForQueryParameters)?.GetType());
        }

        private static Exception CaptureException(Func<string> action)
        {
            try
            {
                action();
                return null;
            }
            catch (Exception e)
            {
                return e;
            }
        }

        private sealed class OverridingResult : AWS4SigningResult
        {
            public OverridingResult(string accessKey, DateTime signedAt, string signedHeaders, string scope, byte[] signature)
                : base(accessKey, "secret", signedAt, signedHeaders, scope, new byte[32], signature)
            {
            }

            public override string Signature => "overridden";
        }
    }
}
