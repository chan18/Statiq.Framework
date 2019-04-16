﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Wyam.Common.Caching;
using Wyam.Common.Configuration;
using Wyam.Common.Documents;
using Wyam.Common.Execution;
using Wyam.Common.IO;
using Wyam.Common.JavaScript;
using Wyam.Common.Meta;
using Wyam.Common.Modules;
using Wyam.Common.Shortcodes;
using Wyam.Common.Tracing;
using Wyam.Common.Util;
using Wyam.Testing.Caching;
using Wyam.Testing.Configuration;
using Wyam.Testing.Documents;
using Wyam.Testing.IO;
using Wyam.Testing.Meta;
using Wyam.Testing.Shortcodes;

namespace Wyam.Testing.Execution
{
    /// <summary>
    /// An <see cref="IExecutionContext"/> that can be used for testing.
    /// </summary>
    public class TestExecutionContext : IExecutionContext, ITypeConversions
    {
        private readonly TestSettings _settings = new TestSettings();

        /// <inheritdoc/>
        public IDocument GetDocument(
            IDocument sourceDocument,
            FilePath source,
            Stream stream,
            IEnumerable<KeyValuePair<string, object>> items = null,
            bool disposeStream = true)
        {
            TestDocument document = sourceDocument == null
                ? new TestDocument(items)
                : new TestDocument(items == null ? sourceDocument : sourceDocument.Concat(items));
            if (sourceDocument != null)
            {
                document.Id = sourceDocument.Id;
            }
            document.Source = source;
            document.Content = GetContent(stream);
            return document;
        }

        private string GetContent(Stream stream)
        {
            if (stream == null)
            {
                return string.Empty;
            }
            stream.Position = 0;
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true, 4096, true))
            {
                return reader.ReadToEnd();
            }
        }

        /// <inheritdoc/>
        public Guid ExecutionId { get; set; } = Guid.NewGuid();

        /// <inheritdoc/>
        public IReadOnlyCollection<byte[]> DynamicAssemblies { get; set; } = new List<byte[]>();

        /// <inheritdoc/>
        public IReadOnlyCollection<string> Namespaces { get; set; } = new List<string>();

        /// <inheritdoc/>
        public IReadOnlyPipeline Pipeline { get; set; }

        /// <inheritdoc/>
        public IModule Module { get; set; }

        /// <inheritdoc/>
        public IExecutionCache ExecutionCache { get; set; } = new TestExecutionCache();

        /// <inheritdoc/>
        public IReadOnlyFileSystem FileSystem { get; set; } = new TestFileSystem();

        /// <inheritdoc/>
        public IDocumentCollection Documents { get; set; }

        /// <inheritdoc/>
        public string ApplicationInput { get; set; }

        /// <inheritdoc/>
        public ISettings Settings => _settings;

        IReadOnlySettings IExecutionContext.Settings => Settings;

        public IShortcodeCollection Shortcodes { get; set; } = new TestShortcodeCollection();

        IReadOnlyShortcodeCollection IExecutionContext.Shortcodes => Shortcodes;

        public IServiceProvider ServiceProvider { get; set; } = new TestServiceProvider();

        /// <inheritdoc/>
        public Task<Stream> GetContentStreamAsync(string content = null) =>
            Task.FromResult<Stream>(string.IsNullOrEmpty(content) ? new MemoryStream() : new MemoryStream(Encoding.UTF8.GetBytes(content)));

        /// <inheritdoc/>
        public HttpClient CreateHttpClient() =>
            new HttpClient(new TestHttpMessageHandler(HttpResponseFunc, null));

        /// <inheritdoc/>
        public HttpClient CreateHttpClient(HttpMessageHandler handler) =>
            new HttpClient(new TestHttpMessageHandler(HttpResponseFunc, handler));

        /// <summary>
        /// A message handler that should be used to register <see cref="HttpResponseMessage"/>
        /// instances for a given request.
        /// </summary>
        public Func<HttpRequestMessage, HttpMessageHandler, HttpResponseMessage> HttpResponseFunc { get; set; }
            = (_, __) => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(string.Empty)
            };

        public Dictionary<(Type Value, Type Result), Func<object, object>> TypeConversions { get; } = new Dictionary<(Type Value, Type Result), Func<object, object>>();

        public void AddTypeConversion<T, TResult>(Func<T, TResult> typeConversion) => TypeConversions.Add((typeof(T), typeof(TResult)), x => typeConversion((T)x));

        /// <inheritdoc/>
        public bool TryConvert<T>(object value, out T result)
        {
            // Check if there's a test-specific conversion
            if (TypeConversions.TryGetValue((value?.GetType() ?? typeof(object), typeof(T)), out Func<object, object> typeConversion))
            {
                result = (T)typeConversion(value);
                return true;
            }

            // Default conversion is just to cast
            if (value is T)
            {
                result = (T)value;
                return true;
            }

            result = default;
            return value == null;
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<IDocument>> ExecuteAsync(IEnumerable<IModule> modules, IEnumerable<IDocument> inputs)
        {
            if (modules == null)
            {
                return Array.Empty<IDocument>();
            }
            foreach (IModule module in modules)
            {
                inputs = await module.ExecuteAsync(inputs.ToList(), this);
            }
            return inputs.ToList();
        }

        public Task<IShortcodeResult> GetShortcodeResultAsync(string content, IEnumerable<KeyValuePair<string, object>> metadata = null)
            => Task.FromResult(GetShortcodeResult(content == null ? null : GetContentStreamAsync(content).Result, metadata));

        public IShortcodeResult GetShortcodeResult(Stream content, IEnumerable<KeyValuePair<string, object>> metadata = null)
            => new ShortcodeResult(content, metadata);

        private class ShortcodeResult : IShortcodeResult
        {
            public Stream Stream { get; }

            public IEnumerable<KeyValuePair<string, object>> Metadata { get; }

            public ShortcodeResult(Stream stream, IEnumerable<KeyValuePair<string, object>> metadata)
            {
                Stream = stream;
                Metadata = metadata;
            }
        }

        public Func<IJavaScriptEngine> JsEngineFunc { get; set; } = () =>
            throw new NotImplementedException("JavaScript test engine not initialized. Wyam.Testing.JavaScript can be used to return a working JavaScript engine");

        /// <inheritdoc/>
        public IJavaScriptEnginePool GetJavaScriptEnginePool(
            Action<IJavaScriptEngine> initializer = null,
            int startEngines = 10,
            int maxEngines = 25,
            int maxUsagesPerEngine = 100,
            TimeSpan? engineTimeout = null) =>
            new TestJsEnginePool(JsEngineFunc, initializer);

        private class TestJsEnginePool : IJavaScriptEnginePool
        {
            private readonly Func<IJavaScriptEngine> _engineFunc;
            private readonly Action<IJavaScriptEngine> _initializer;

            public TestJsEnginePool(Func<IJavaScriptEngine> engineFunc, Action<IJavaScriptEngine> initializer)
            {
                _engineFunc = engineFunc;
                _initializer = initializer;
            }

            public IJavaScriptEngine GetEngine(TimeSpan? timeout = null)
            {
                IJavaScriptEngine engine = _engineFunc();
                _initializer?.Invoke(engine);
                return engine;
            }

            public void Dispose()
            {
            }

            public void RecycleEngine(IJavaScriptEngine engine)
            {
                throw new NotImplementedException();
            }

            public void RecycleAllEngines()
            {
                throw new NotImplementedException();
            }
        }

        // Service Provider

        public object GetRequiredService(Type serviceType) => ServiceProvider.GetRequiredService(serviceType);

        public T GetRequiredService<T>() => ServiceProvider.GetRequiredService<T>();

        public T GetService<T>() => ServiceProvider.GetService<T>();

        public IEnumerable<T> GetServices<T>() => ServiceProvider.GetServices<T>();

        public IEnumerable<object> GetServices(Type serviceType) => ServiceProvider.GetServices(serviceType);

        // IMetadata

        /// <inheritdoc/>
        public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
        {
            return _settings.GetEnumerator();
        }

        /// <inheritdoc/>
        IEnumerator IEnumerable.GetEnumerator()
        {
            return ((IEnumerable)_settings).GetEnumerator();
        }

        /// <inheritdoc/>
        public int Count => _settings.Count;

        /// <inheritdoc/>
        public bool ContainsKey(string key) => _settings.ContainsKey(key);

        /// <inheritdoc/>
        public object this[string key] => _settings[key];

        /// <inheritdoc/>
        public IEnumerable<string> Keys => _settings.Keys;

        /// <inheritdoc/>
        public IEnumerable<object> Values => _settings.Values;

        /// <inheritdoc/>
        public IMetadata<T> MetadataAs<T>() => _settings.MetadataAs<T>();

        /// <inheritdoc/>
        public object Get(string key, object defaultValue = null) => _settings.Get(key, defaultValue);

        /// <inheritdoc/>
        public object GetRaw(string key) => _settings.GetRaw(key);

        /// <inheritdoc/>
        public T Get<T>(string key) => _settings.Get<T>(key);

        /// <inheritdoc/>
        public T Get<T>(string key, T defaultValue) => _settings.Get(key, defaultValue);

        /// <inheritdoc/>
        public bool TryGetValue<T>(string key, out T value) => _settings.TryGetValue<T>(key, out value);

        /// <inheritdoc/>
        public bool TryGetValue(string key, out object value) => _settings.TryGetValue(key, out value);

        public IMetadata GetMetadata(params string[] keys) => _settings.GetMetadata(keys);
    }
}
