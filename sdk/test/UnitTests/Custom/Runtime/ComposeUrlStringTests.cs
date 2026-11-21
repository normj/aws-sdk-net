using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Util;
using AWSSDK_DotNet.UnitTests;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Text;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins <see cref="AmazonServiceClient.ComposeUrlString"/> (used by HttpHandler for every request, and by
    /// <see cref="AmazonServiceClient.ComposeUrl(IRequest)"/>) to the output of the StringBuilder/AppendFormat
    /// implementation it replaced.
    /// </summary>
    [TestClass]
    [TestCategory("UnitTest")]
    [TestCategory("Runtime")]
    public class ComposeUrlStringTests
    {
        // The implementation before the builder was made lazy, kept verbatim as the reference.
        private static string Reference(IRequest internalRequest)
        {
            Uri url = internalRequest.Endpoint;
            var resourcePath = internalRequest.ResourcePath;
            if (resourcePath == null)
                resourcePath = string.Empty;
            else
            {
                if (resourcePath.StartsWith("/", StringComparison.Ordinal))
                    resourcePath = resourcePath.Substring(1);
                resourcePath = AWSSDKUtils.ResolveResourcePathV2(resourcePath, internalRequest.PathResources);
            }

            var delim = "?";
            var sb = new StringBuilder();

            if (internalRequest.SubResources?.Count > 0)
            {
                foreach (var subResource in internalRequest.SubResources)
                {
                    sb.AppendFormat("{0}{1}", delim, subResource.Key);
                    if (subResource.Value != null)
                        sb.AppendFormat("={0}", subResource.Value);
                    delim = "&";
                }
            }

            if (internalRequest.UseQueryString && internalRequest.Parameters?.Count > 0)
            {
                var queryString = AWSSDKUtils.GetParametersAsString(internalRequest);
                sb.AppendFormat("{0}{1}", delim, queryString);
            }

            var parameterizedPath = string.Concat(resourcePath, sb);
            var hasSlash = url.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) || parameterizedPath.StartsWith("/", StringComparison.Ordinal);

            return hasSlash
                ? url.AbsoluteUri + parameterizedPath
                : url.AbsoluteUri + "/" + parameterizedPath;
        }

        private static IRequest CreateRequest(string endpoint, string resourcePath)
        {
            var request = new DefaultRequest(new MockAmazonWebServiceRequest(), "TestService");
            request.Endpoint = new Uri(endpoint);
            request.ResourcePath = resourcePath;
            return request;
        }

        private static IEnumerable<IRequest> Requests()
        {
            foreach (var endpoint in new[] { "https://dynamodb.us-west-2.amazonaws.com", "https://s3.amazonaws.com/", "https://example.com/base/path", "http://localhost:8080/" })
            {
                foreach (var resourcePath in new[] { null, "", "/", "/2015-03-31/functions/my-function/invocations", "no-leading-slash/x", "//double", "/a b/{c}" })
                {
                    yield return CreateRequest(endpoint, resourcePath);

                    var withSubResources = CreateRequest(endpoint, resourcePath);
                    withSubResources.AddSubResource("versioning");
                    withSubResources.AddSubResource("versionId", "3HL4kqtJlcpXroDTDmJ.rmSpXd3dIbrHY");
                    withSubResources.AddSubResource("braces{0}", "va{1}lue");
                    yield return withSubResources;

                    var withQuery = CreateRequest(endpoint, resourcePath);
                    withQuery.UseQueryString = true;
                    withQuery.Parameters["Action"] = "ListThings";
                    withQuery.Parameters["Name"] = "a b&c={0}";
                    yield return withQuery;

                    var withBoth = CreateRequest(endpoint, resourcePath);
                    withBoth.AddSubResource("uploads");
                    withBoth.UseQueryString = true;
                    withBoth.Parameters["max-uploads"] = "10";
                    yield return withBoth;

                    var parametersWithoutQueryString = CreateRequest(endpoint, resourcePath);
                    parametersWithoutQueryString.Parameters["Action"] = "ListThings";
                    yield return parametersWithoutQueryString;

                    var queryStringWithoutParameters = CreateRequest(endpoint, resourcePath);
                    queryStringWithoutParameters.UseQueryString = true;
                    yield return queryStringWithoutParameters;
                }
            }

            var withPathResources = CreateRequest("https://s3.us-west-2.amazonaws.com", "/{Bucket}/{Key+}");
            withPathResources.AddPathResource("{Bucket}", "my-bucket");
            withPathResources.AddPathResource("{Key+}", "photos/2026/img 01.jpg");
            withPathResources.AddSubResource("tagging");
            yield return withPathResources;
        }

        [TestMethod]
        public void ComposeUrlString_MatchesPreviousImplementation()
        {
            foreach (var request in Requests())
            {
                var expected = Reference(request);
                Assert.AreEqual(expected, AmazonServiceClient.ComposeUrlString(request), request.Endpoint + " " + request.ResourcePath);
                Assert.AreEqual(AmazonServiceClient.CreateRequestUri(expected).AbsoluteUri, AmazonServiceClient.ComposeUrl(request).AbsoluteUri);
            }
        }

        [TestMethod]
        public void ComposeUrlString_SimpleRequest()
        {
            Assert.AreEqual("https://dynamodb.us-west-2.amazonaws.com/", AmazonServiceClient.ComposeUrlString(CreateRequest("https://dynamodb.us-west-2.amazonaws.com", "/")));

            var request = CreateRequest("https://s3.amazonaws.com", "/bucket");
            request.AddSubResource("acl");
            request.AddSubResource("versionId", "v1");
            Assert.AreEqual("https://s3.amazonaws.com/bucket?acl&versionId=v1", AmazonServiceClient.ComposeUrlString(request));
        }
    }
}
