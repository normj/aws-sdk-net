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
using Amazon.Runtime;
using Amazon.Runtime.Endpoints;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins the endpoint BaseEndpointResolver sets on the request when consecutive requests resolve to the same URL
    /// (the resolver reuses the previous URL string and Uri) and when the URL changes between requests.
    /// </summary>
    [TestClass]
    [TestCategory("UnitTest")]
    [TestCategory("Endpoints")]
    public class EndpointUriReuseTests
    {
        /// <summary>
        /// Returns a newly built URL string on every call, as the generated rule providers do.
        /// </summary>
        private class SequenceEndpointProvider : IEndpointProvider
        {
            public Func<string> NextUrl;

            public Endpoint ResolveEndpoint(EndpointParameters parameters)
            {
                return new Endpoint(new string(NextUrl().ToCharArray()));
            }
        }

        private static ExecutionContext CreateExecutionContext(IClientConfig config, string resourcePath = null)
        {
            var requestContext = new RequestContext(false, new NullSigner())
            {
                ClientConfig = config,
                Request = new DefaultRequest(new MockAmazonWebServiceRequest(), "test-service") { ResourcePath = resourcePath }
            };
            return new ExecutionContext(requestContext, null);
        }

        private static Uri Resolve(BaseEndpointResolver resolver, IClientConfig config, string resourcePath = null)
        {
            var executionContext = CreateExecutionContext(config, resourcePath);
            resolver.ProcessRequestHandlers(executionContext);
            return executionContext.RequestContext.Request.Endpoint;
        }

        private static void AssertSameUri(Uri expected, Uri actual)
        {
            Assert.AreEqual(expected, actual);
            Assert.AreEqual(expected.OriginalString, actual.OriginalString);
            Assert.AreEqual(expected.AbsoluteUri, actual.AbsoluteUri);
            Assert.AreEqual(expected.ToString(), actual.ToString());
            Assert.AreEqual(expected.Host, actual.Host);
            Assert.AreEqual(expected.Port, actual.Port);
            Assert.AreEqual(expected.IsDefaultPort, actual.IsDefaultPort);
            Assert.AreEqual(expected.Scheme, actual.Scheme);
            Assert.AreEqual(expected.AbsolutePath, actual.AbsolutePath);
        }

        [TestMethod]
        public void SameUrlOnConsecutiveRequestsGivesTheSameEndpoint()
        {
            var provider = new SequenceEndpointProvider { NextUrl = () => "https://service.us-west-2.amazonaws.com" };
            var config = new MockClientConfig { EndpointProvider = provider };
            var resolver = new BaseEndpointResolver();

            for (int i = 0; i < 3; i++)
            {
                AssertSameUri(new Uri("https://service.us-west-2.amazonaws.com/"), Resolve(resolver, config));
                AssertSameUri(new Uri("https://service.us-west-2.amazonaws.com/"), Resolve(resolver, config, "/"));
            }
        }

        [TestMethod]
        public void ResolvedEndpointUrlGetsOneTrailingSlash()
        {
            var provider = new SequenceEndpointProvider { NextUrl = () => "https://service.us-west-2.amazonaws.com" };
            var config = new MockClientConfig { EndpointProvider = provider };
            var resolver = new BaseEndpointResolver();

            for (int i = 0; i < 3; i++)
            {
                var executionContext = CreateExecutionContext(config);
                Assert.AreEqual("https://service.us-west-2.amazonaws.com/", resolver.GetEndpoint(executionContext).URL);
            }

            // A URL that already ends with a slash, or a request with a resource path, is left alone.
            provider.NextUrl = () => "https://service.us-west-2.amazonaws.com/";
            Assert.AreEqual("https://service.us-west-2.amazonaws.com/", resolver.GetEndpoint(CreateExecutionContext(config)).URL);
            provider.NextUrl = () => "https://service.us-west-2.amazonaws.com";
            Assert.AreEqual("https://service.us-west-2.amazonaws.com", resolver.GetEndpoint(CreateExecutionContext(config, "/items/{id}")).URL);
            Assert.AreEqual("https://service.us-west-2.amazonaws.com/", resolver.GetEndpoint(CreateExecutionContext(config)).URL);
        }

        [TestMethod]
        public void ChangedUrlBetweenRequestsGivesTheNewEndpoint()
        {
            var urls = new[]
            {
                "https://a.us-west-2.amazonaws.com",
                "https://b.us-west-2.amazonaws.com",
                "https://a.us-west-2.amazonaws.com",
                "https://a.us-west-2.amazonaws.com:8443",
                "http://a.us-west-2.amazonaws.com",
                "https://A.US-WEST-2.amazonaws.com",
                "https://a.us-west-2.amazonaws.com/prefix/",
                "https://bucket.s3.us-west-2.amazonaws.com/",
                "https://a.us-west-2.amazonaws.com",
            };
            int next = 0;
            var provider = new SequenceEndpointProvider { NextUrl = () => urls[next++] };
            var config = new MockClientConfig { EndpointProvider = provider };
            var resolver = new BaseEndpointResolver();

            foreach (var url in urls)
            {
                var expected = new Uri(url.EndsWith("/") ? url : url + "/");
                AssertSameUri(expected, Resolve(resolver, config));
            }
        }

        [TestMethod]
        public void InvalidUrlThrowsEveryTime()
        {
            var url = "https://service.us-west-2.amazonaws.com";
            var provider = new SequenceEndpointProvider { NextUrl = () => url };
            var config = new MockClientConfig { EndpointProvider = provider };
            var resolver = new BaseEndpointResolver();

            Resolve(resolver, config);
            url = "not a url";
            Assert.ThrowsExactly<UriFormatException>(() => Resolve(resolver, config));
            Assert.ThrowsExactly<UriFormatException>(() => Resolve(resolver, config));
            url = "https://service.us-west-2.amazonaws.com";
            AssertSameUri(new Uri("https://service.us-west-2.amazonaws.com/"), Resolve(resolver, config));
        }

        [TestMethod]
        public void UseHttpStillAppliesToTheReusedEndpoint()
        {
            var provider = new SequenceEndpointProvider { NextUrl = () => "https://localhost:7777" };
            var config = new MockClientConfig { EndpointProvider = provider, UseHttp = true };
            var resolver = new BaseEndpointResolver();

            for (int i = 0; i < 3; i++)
            {
                var endpoint = Resolve(resolver, config);
                Assert.AreEqual("http", endpoint.Scheme);
                Assert.AreEqual(7777, endpoint.Port);
            }
        }

        [TestMethod]
        public void HelpersMatchTheExpressionsTheyReplace()
        {
            var resolver = new BaseEndpointResolver();
            foreach (var url in new[] { "https://a.example.com", "https://a.example.com", "https://b.example.com/x", "", "https://a.example.com" })
            {
                Assert.AreEqual(url + "/", resolver.AppendSlash(url));
                Assert.AreEqual(url + "/", resolver.AppendSlash(new string(url.ToCharArray())));
            }

            foreach (var url in new[] { "https://a.example.com/", "https://a.example.com/", "http://b.example.com:81/x/", "https://a.example.com/" })
            {
                AssertSameUri(new Uri(url), resolver.GetEndpointUri(url));
                AssertSameUri(new Uri(url), resolver.GetEndpointUri(new string(url.ToCharArray())));
            }

            Assert.ThrowsExactly<ArgumentNullException>(() => resolver.GetEndpointUri(null));
            Assert.ThrowsExactly<UriFormatException>(() => resolver.GetEndpointUri(""));
            Assert.ThrowsExactly<UriFormatException>(() => resolver.GetEndpointUri("relative/path"));
        }

        [TestMethod]
        public void ConcurrentRequestsWithDifferentUrlsEachGetTheirOwnEndpoint()
        {
            var resolver = new BaseEndpointResolver();
            var urls = Enumerable.Range(0, 4).Select(i => "https://service" + i + ".us-west-2.amazonaws.com").ToArray();

            Parallel.For(0, 20000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                var url = urls[i % urls.Length];
                var provider = new SequenceEndpointProvider { NextUrl = () => url };
                var config = new MockClientConfig { EndpointProvider = provider };
                var endpoint = Resolve(resolver, config);
                if (endpoint.AbsoluteUri != url + "/" || endpoint.Host != new Uri(url).Host)
                {
                    throw new InvalidOperationException("Wrong endpoint " + endpoint + " for " + url);
                }
            });
        }
    }
}
