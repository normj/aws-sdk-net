using System;
using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins the x-amz-content-sha256 value that AWS4Signer.SetRequestBodyHash computes from the request
    /// payload bytes to the lowercase hex SHA-256 of those bytes.
    /// </summary>
    [TestClass]
    public class AWS4SignerPayloadHashTests
    {
        private static string Reference(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(data);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                    builder.Append(b.ToString("x2"));
                return builder.ToString();
            }
        }

        private static IRequest NewRequest(byte[] content)
        {
            return new DefaultRequest(new StubRequest(), "Test")
            {
                HttpMethod = "POST",
                Endpoint = new Uri("https://example.us-west-2.amazonaws.com"),
                ResourcePath = "/",
                Content = content,
            };
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void ContentHashMatchesReference()
        {
            var random = new Random(20261121);
            foreach (var length in new[] { 1, 2, 31, 32, 33, 55, 56, 63, 64, 65, 100, 1000, 4096, 1 << 20 })
            {
                var content = new byte[length];
                random.NextBytes(content);
                var request = NewRequest(content);

                var hash = AWS4Signer.SetRequestBodyHash(request, true, "STREAMING", 80);

                Assert.AreEqual(Reference(content), hash, "length " + length);
                Assert.AreEqual(hash, request.Headers[Amazon.Util.HeaderKeys.XAmzContentSha256Header]);
                Assert.AreEqual(64, hash.Length);
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void EmptyAndParameterBodiesMatchReference()
        {
            var empty = NewRequest(new byte[0]);
            Assert.AreEqual(Reference(new byte[0]), AWS4Signer.SetRequestBodyHash(empty, true, "STREAMING", 80));
            Assert.AreEqual("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", empty.Headers[Amazon.Util.HeaderKeys.XAmzContentSha256Header]);

            // No Content: the form-encoded parameters are hashed.
            var form = NewRequest(null);
            form.Parameters["Action"] = "GetCallerIdentity";
            form.Parameters["Version"] = "2011-06-15";
            var hash = AWS4Signer.SetRequestBodyHash(form, true, "STREAMING", 80);
            Assert.AreEqual(Reference(Encoding.UTF8.GetBytes("Action=GetCallerIdentity&Version=2011-06-15")), hash);

            // Same request hashed twice (a retry) gives the same value.
            form.Headers.Remove(Amazon.Util.HeaderKeys.XAmzContentSha256Header);
            Assert.AreEqual(hash, AWS4Signer.SetRequestBodyHash(form, true, "STREAMING", 80));
        }

        private sealed class StubRequest : AmazonWebServiceRequest { }
    }
}
