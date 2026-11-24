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
using System.Collections.Generic;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// DefaultRequest creates its parameter collection, event stream headers, sub-resources, path resources
    /// and trailing headers on first access. These tests cover requests that use more than one of them, so the
    /// URL and the canonical query still combine sub-resources with query parameters as before, and creating
    /// one collection never creates another.
    /// </summary>
    [TestClass]
    public class DefaultRequestLazyCombinedTests
    {
        private sealed class TestRequest : AmazonWebServiceRequest { }

        private static DefaultRequest NewRequest(string resourcePath = "/")
        {
            return new DefaultRequest(new TestRequest(), "TestService")
            {
                Endpoint = new Uri("https://service.us-west-2.amazonaws.com"),
                ResourcePath = resourcePath,
                HttpMethod = "GET",
            };
        }

        [TestMethod]
        public void UsingTheParameterCollectionAndEventHeaders_DoesNotCreateTheResourceMaps()
        {
            var request = NewRequest();
            request.Parameters["a"] = "1";
            request.ParameterCollection.Add("b", "2");
            request.EventHeaders.Add(new Amazon.Runtime.EventStreams.EventStreamHeader("h"));

            Assert.IsNull(request.SubResourcesIfCreated);
            Assert.IsNull(request.PathResourcesIfCreated);
            Assert.IsNull(request.TrailingHeadersIfCreated);
            Assert.AreEqual(2, request.Parameters.Count);
            Assert.AreEqual(1, request.EventHeaders.Count);
        }

        [TestMethod]
        public void UsingTheResourceMaps_LeavesTheParameterCollectionAndEventHeadersEmpty()
        {
            var request = NewRequest();
            request.AddSubResource("acl");
            request.AddPathResource("{Bucket}", "b");
            request.TrailingHeaders["x-amz-checksum-crc32"] = string.Empty;

            Assert.AreEqual(0, request.Parameters.Count);
            Assert.AreEqual(0, request.ParameterCollection.Count);
            Assert.AreEqual(0, request.EventHeaders.Count);
            Assert.AreSame(request.ParameterCollection, request.ParameterCollection);
        }

        [TestMethod]
        public void ComposeUrl_CombinesSubResourcesAndQueryParameters()
        {
            var request = NewRequest("/{Bucket}");
            request.UseQueryString = true;
            request.AddPathResource("{Bucket}", "my-bucket");
            request.AddSubResource("acl");
            request.Parameters["max-keys"] = "5";

            Assert.AreEqual("https://service.us-west-2.amazonaws.com/my-bucket?acl&max-keys=5",
                AmazonServiceClient.ComposeUrl(request).AbsoluteUri);
        }

        [TestMethod]
        public void ComposeUrl_QueryParametersWithoutSubResources_LeavesSubResourcesUncreated()
        {
            var request = NewRequest("/items");
            request.UseQueryString = true;
            request.Parameters["max-keys"] = "5";

            Assert.AreEqual("https://service.us-west-2.amazonaws.com/items?max-keys=5",
                AmazonServiceClient.ComposeUrl(request).AbsoluteUri);
            Assert.IsNull(request.SubResourcesIfCreated);
            Assert.IsNull(request.PathResourcesIfCreated);
        }

        [TestMethod]
        public void GetParametersToCanonicalize_PutsSubResourcesBeforeQueryParameters()
        {
            var request = NewRequest();
            request.UseQueryString = true;
            request.Parameters["b"] = "2";
            request.Parameters["a"] = "1";
            Assert.AreEqual(2, AWS4SignerTestHook.Parameters(request).Count);
            Assert.IsNull(request.SubResourcesIfCreated);

            request.AddSubResource("uploads");
            CollectionAssert.AreEqual(
                new[]
                {
                    new KeyValuePair<string, string>("uploads", null),
                    new KeyValuePair<string, string>("a", "1"),
                    new KeyValuePair<string, string>("b", "2"),
                },
                AWS4SignerTestHook.Parameters(request));
        }

        private sealed class AWS4SignerTestHook : AWS4Signer
        {
            public static List<KeyValuePair<string, string>> Parameters(IRequest request) => GetParametersToCanonicalize(request);
        }
    }
}
