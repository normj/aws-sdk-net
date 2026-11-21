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
using Amazon.Runtime.Internal.Endpoints.StandardLibrary;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins what <see cref="Fn.ParseURL(string)"/> returns, on the first call for a URL and on
    /// repeat calls, and that every call gets its own mutable result.
    /// </summary>
    [TestClass]
    public class EndpointsParseUrlTests
    {
        private static readonly string[] Urls =
        {
            "https://vpce-0a1b2c3d4e5f.s3.us-west-2.vpce.amazonaws.com",
            "http://localhost:4566/custom/path",
            "http://localhost:4566/custom/path/",
            "https://example.com",
            "https://example.com/",
            "https://example.com:443",
            "https://example.com:8443/",
            "http://example.com:80/a/b",
            "HTTPS://EXAMPLE.COM/Path",
            "https://example.com/a%20b/c",
            "https://example.com/a b",
            "https://example.com/a/../b/./c",
            "https://user:pass@example.com/x",
            "https://example.com/#fragment",
            "https://127.0.0.1",
            "https://127.0.0.1:8443/path",
            "http://[::1]:8080/",
            "http://[fe80::1]/x",
            "https://123.45.67.890",
            "wss://example.com/socket",
            "https://bücher.example/",
            "https://example.com/über",
            // Rejected
            "https://example.com/?query",
            "https://example.com/path?a=b",
            "ftp://example.com/",
            "file:///tmp/x",
            "example.com",
            "/relative/path",
            "",
            "not a url",
            "https://",
        };

        private static readonly string[] Keys = { "scheme", "authority", "path", "normalizedPath", "isIp" };

        [TestMethod]
        public void MatchesUriBasedParsing()
        {
            foreach (var literal in Urls)
            {
                // A copy that is not interned, so nothing earlier in the run can have cached it by reference.
                var url = new string(literal.ToCharArray());
                var expected = Describe(OriginalParseURL(url));
                for (int call = 0; call < 3; call++)
                {
                    Assert.AreEqual(expected, Describe(Fn.ParseURL(url)), $"url \"{literal}\", call {call}");
                    Assert.AreEqual(expected, Describe(Fn.ParseURL(literal)), $"literal url \"{literal}\", call {call}");
                }
            }
        }

        [TestMethod]
        public void NullReturnsNull()
        {
            Assert.IsNull(Fn.ParseURL(null));
            Assert.IsNull(Fn.ParseURL(null));
        }

        [TestMethod]
        public void EachCallReturnsAnIndependentMutableCopy()
        {
            const string url = "https://mutable.example.com/base";
            var first = Fn.ParseURL(url);
            var second = Fn.ParseURL(url);
            Assert.AreNotSame(first, second);

            first.scheme = "changed";
            first.path = "/changed";
            first.isIp = true;
            first["extra"] = 1;

            Assert.AreEqual("https", second.scheme);
            var third = Fn.ParseURL(url);
            Assert.AreEqual("https", third.scheme);
            Assert.AreEqual("mutable.example.com", third.authority);
            Assert.AreEqual("/base", third.path);
            Assert.AreEqual("/base/", third.normalizedPath);
            Assert.IsFalse(third.isIp);
            Assert.IsNull(third["extra"]);
            Assert.AreEqual("mutable.example.com", Fn.GetAttr(third, "authority"));
        }

        [TestMethod]
        public void ConcurrentCallsReturnCorrectResults()
        {
            Parallel.For(0, 2000, i =>
            {
                var url = Fn.ParseURL("https://concurrent.example.com/p" + (i % 7));
                Assert.AreEqual("concurrent.example.com", url.authority);
                Assert.AreEqual("/p" + (i % 7), url.path);
                url.path = "mutated";
            });
        }

        [TestMethod]
        public void ManyDistinctUrlsStayCorrect()
        {
            for (int i = 0; i < 1000; i++)
            {
                var url = "https://host" + i + ".example.com/p" + i;
                var parsed = Fn.ParseURL(url);
                Assert.AreEqual("host" + i + ".example.com", parsed.authority);
                Assert.AreEqual("/p" + i, parsed.path);
                Assert.AreEqual("/p" + i + "/", parsed.normalizedPath);
                Assert.AreEqual(Describe(OriginalParseURL(url)), Describe(Fn.ParseURL(url)));
            }
        }

        private static string Describe(URL url)
        {
            if (url == null)
            {
                return "<null>";
            }
            var parts = new List<string>();
            foreach (var key in Keys)
            {
                var value = url[key];
                parts.Add(key + "=" + (value == null ? "<null>" : value.GetType().Name + ":" + value));
            }
            return string.Join("|", parts);
        }

        // Copy of Fn.ParseURL before results were cached.
        private static URL OriginalParseURL(string url)
        {
            var supportedSchemas = new HashSet<string>(StringComparer.Ordinal) { "http", "https", "wss" };
            Uri uri;
            Uri.TryCreate(url, UriKind.Absolute, out uri);
            if (uri == null || uri.Query?.Length > 0 || !supportedSchemas.Contains(uri.Scheme))
            {
                return null;
            }

            var result = new URL
            {
                scheme = uri.Scheme,
                authority = uri.Authority,
                path = uri.GetComponents(UriComponents.Path, UriFormat.Unescaped)
            };

            if (result.path.Length > 0)
            {
                result.path = "/" + result.path;
            }

            result.normalizedPath = uri.PathAndQuery;
            if (result.normalizedPath.Length > 1)
            {
                result.normalizedPath += "/";
            }
            var hostNameType = Uri.CheckHostName(uri.Host);
            result.isIp = hostNameType == UriHostNameType.IPv4 || hostNameType == UriHostNameType.IPv6;
            return result;
        }
    }
}
