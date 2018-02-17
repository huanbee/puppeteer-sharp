﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PuppeteerSharp.Input;
using PuppeteerSharp.Helpers;
using System.IO;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace PuppeteerSharp
{
    public class Page
    {
        private Session _client;
        private bool _ignoreHTTPSErrors;
        private NetworkManager _networkManager;
        private FrameManager _frameManager;
        private TaskQueue _screenshotTaskQueue;
        private EmulationManager _emulationManager;
        private ViewPortOptions _viewport;
        private Mouse _mouse;
        private Dictionary<string, Func<object>> _pageBindings;
        private const int DefaultNavigationTimeout = 30000;

        private static Dictionary<string, PaperFormat> _paperFormats = new Dictionary<string, PaperFormat> {
            {"letter", new PaperFormat {Width = 8.5m, Height = 11}},
            {"legal", new PaperFormat {Width = 8.5m, Height = 14}},
            {"tabloid", new PaperFormat {Width = 11, Height = 17}},
            {"ledger", new PaperFormat {Width = 17, Height = 11}},
            {"a0", new PaperFormat {Width = 33.1m, Height = 46.8m }},
            {"a1", new PaperFormat {Width = 23.4m, Height = 33.1m }},
            {"a2", new PaperFormat {Width = 16.5m, Height = 23.4m }},
            {"a3", new PaperFormat {Width = 11.7m, Height = 16.5m }},
            {"a4", new PaperFormat {Width = 8.27m, Height = 11.7m }},
            {"a5", new PaperFormat {Width = 5.83m, Height = 8.27m }},
            {"a6", new PaperFormat {Width = 4.13m, Height = 5.83m }},
        };

        private static Dictionary<string, decimal> _unitToPixels = new Dictionary<string, decimal> {
            {"px", 1},
            {"in", 96},
            {"cm", 37.8m},
            {"mm", 3.78m}
        };

        private Page(Session client, FrameTree frameTree, bool ignoreHTTPSErrors, TaskQueue screenshotTaskQueue)
        {
            _client = client;

            Keyboard = new Keyboard(client);
            _mouse = new Mouse(client, Keyboard);
            Touchscreen = new Touchscreen(client, Keyboard);
            _frameManager = new FrameManager(client, frameTree, this);
            _networkManager = new NetworkManager(client);
            _emulationManager = new EmulationManager(client);
            Tracing = new Tracing(client);
            _pageBindings = new Dictionary<string, Func<object>>();

            _ignoreHTTPSErrors = ignoreHTTPSErrors;

            _screenshotTaskQueue = screenshotTaskQueue;

            //TODO: Do we need this bubble?
            _frameManager.FrameAttached += (sender, e) => FrameAttached?.Invoke(this, e);
            _frameManager.FrameDetached += (sender, e) => FrameDetached?.Invoke(this, e);
            _frameManager.FrameNavigated += (sender, e) => FrameNavigated?.Invoke(this, e);

            _networkManager.RequestCreated += (sender, e) => RequestCreated?.Invoke(this, e);
            _networkManager.RequestFailed += (sender, e) => RequestFailed?.Invoke(this, e);
            _networkManager.ResponseCreated += (sender, e) => ResponseCreated?.Invoke(this, e);
            _networkManager.RequestFinished += (sender, e) => RequestFinished?.Invoke(this, e);

            _client.MessageReceived += client_MessageReceived;
        }

        #region Public Properties
        public event EventHandler<EventArgs> Load;
        public event EventHandler<ErrorEventArgs> Error;

        public event EventHandler<FrameEventArgs> FrameAttached;
        public event EventHandler<FrameEventArgs> FrameDetached;
        public event EventHandler<FrameEventArgs> FrameNavigated;

        public event EventHandler<ResponseCreatedArgs> ResponseCreated;
        public event EventHandler<RequestEventArgs> RequestCreated;
        public event EventHandler<RequestEventArgs> RequestFinished;
        public event EventHandler<RequestEventArgs> RequestFailed;

        public Frame MainFrame => _frameManager.MainFrame;
        public IEnumerable<Frame> Frames => _frameManager.Frames.Values;
        public string Url => MainFrame.Url;

        public Keyboard Keyboard { get; internal set; }
        public Touchscreen Touchscreen { get; internal set; }
        public Tracing Tracing { get; internal set; }


        #endregion

        #region Public Methods

        public async Task TapAsync(string selector)
        {
            var handle = await GetElementAsync(selector);

            if (handle != null)
            {
                await handle.TapAsync();
                await handle.DisposeAsync();
            }
        }

        public async Task<ElementHandle> GetElementAsync(string selector)
        {
            return await MainFrame.GetElementAsync(selector);
        }

        public async Task<IEnumerable<ElementHandle>> GetElementsAsync(string selector)
        {
            return await MainFrame.GetElementsAsync(selector);
        }

        public async Task<JSHandle> EvaluateHandle(Func<object> pageFunction, params object[] args)
        {
            var context = await MainFrame.GetExecutionContextAsync();
            return await context.EvaluateHandleAsync(pageFunction, args);
        }

        public async Task<JSHandle> EvaluateHandle(string pageFunction, params object[] args)
        {
            var context = await MainFrame.GetExecutionContextAsync();
            return await context.EvaluateHandleAsync(pageFunction, args);
        }

        public async Task<JSHandle> QueryObjects(JSHandle prototypeHandle)
        {
            var context = await MainFrame.GetExecutionContextAsync();
            return await context.QueryObjects(prototypeHandle);
        }


        public async Task<object> EvalAsync(string selector, Func<object> pageFunction, params object[] args)
        {
            return await MainFrame.Eval(selector, pageFunction, args);
        }

        public async Task<object> EvalAsync(string selector, string pageFunction, params object[] args)
        {
            return await MainFrame.Eval(selector, pageFunction, args);
        }

        public async Task SetRequestInterceptionAsync(bool value)
        {
            await _networkManager.SetRequestInterceptionAsync(value);
        }

        public async Task SetOfflineModeAsync(bool value)
        {
            await _networkManager.SetOfflineModeAsync(value);
        }

        public async Task<object> EvalManyAsync(string selector, Func<object> pageFunction, params object[] args)
        {
            return await MainFrame.EvalMany(selector, pageFunction, args);
        }

        public async Task<object> EvalManyAsync(string selector, string pageFunction, params object[] args)
        {
            return await MainFrame.EvalMany(selector, pageFunction, args);
        }

        public async Task<IEnumerable<CookieParam>> GetCookiesAsync(params string[] urls)
        {
            return await _client.SendAsync<List<CookieParam>>("Network.getCookies", new Dictionary<string, object>
            {
                { "urls", urls.Length > 0 ? urls : (object)Url}
            });
        }

        public async Task SetCookieAsync(params CookieParam[] cookies)
        {
            foreach (var cookie in cookies)
            {
                if (string.IsNullOrEmpty(cookie.Url) && Url.StartsWith("http", StringComparison.Ordinal))
                {
                    cookie.Url = Url;
                }
            }

            await DeleteCookieAsync(cookies);

            if (cookies.Length > 0)
            {
                await _client.SendAsync("Network.setCookies", new Dictionary<string, object>
                {
                    { "cookies", cookies}
                });
            }
        }

        public async Task DeleteCookieAsync(params CookieParam[] cookies)
        {
            var pageURL = Url;
            foreach (var cookie in cookies)
            {
                if (string.IsNullOrEmpty(cookie.Url) && pageURL.StartsWith("http", StringComparison.Ordinal))
                {
                    cookie.Url = pageURL;
                }
                await _client.SendAsync("Network.deleteCookies", cookie);
            }
        }

        public async Task<ElementHandle> AddScriptTagAsync(dynamic options)
        {
            return await MainFrame.AddScriptTag(options);
        }

        public async Task<ElementHandle> AddStyleTagAsync(dynamic options)
        {
            return await MainFrame.AddStyleTag(options);
        }

        public static async Task<Page> CreateAsync(Session client, bool ignoreHTTPSErrors, bool appMode,
                                                   TaskQueue screenshotTaskQueue)
        {
            await client.SendAsync("Page.enable", null);
            dynamic result = await client.SendAsync("Page.getFrameTree");
            var page = new Page(client, new FrameTree(result.frameTree), ignoreHTTPSErrors, screenshotTaskQueue);

            await Task.WhenAll(
                client.SendAsync("Page.setLifecycleEventsEnabled", new Dictionary<string, object>
                {
                    {"enabled", true }
                }),
                client.SendAsync("Network.enable", null),
                client.SendAsync("Runtime.enable", null),
                client.SendAsync("Security.enable", null),
                client.SendAsync("Performance.enable", null)
            );

            if (ignoreHTTPSErrors)
            {
                await client.SendAsync("Security.setOverrideCertificateErrors", new Dictionary<string, object>
                {
                    {"override", true}
                });
            }

            // Initialize default page size.
            if (!appMode)
            {
                await page.SetViewport(new ViewPortOptions
                {
                    Width = 800,
                    Height = 600
                });
            }
            return page;
        }

        public async Task<dynamic> GoToAsync(string url, Dictionary<string, string> options = null)
        {
            var referrer = _networkManager.ExtraHTTPHeaders?.GetValueOrDefault("referer");
            var requests = new Dictionary<string, Request>();

            EventHandler<RequestEventArgs> createRequestEventListener = (object sender, RequestEventArgs e) =>
                requests.Add(e.Request.Url, e.Request);

            _networkManager.RequestCreated += createRequestEventListener;

            var mainFrame = _frameManager.MainFrame;
            var timeout = options != null && options.ContainsKey("timeout") ?
                Convert.ToInt32(options["timeout"]) :
                DefaultNavigationTimeout;

            var watcher = new NavigationWatcher(_frameManager, mainFrame, timeout, options);
            var navigateTask = Navigate(_client, url, referrer);

            await Task.WhenAll(
                navigateTask,
                watcher.NavigationTask
            );

            var exception = navigateTask.Exception ?? watcher.NavigationTask.Exception;

            watcher.Cancel();
            _networkManager.RequestCreated -= createRequestEventListener;

            if (exception != null)
            {
                throw new NavigationException(exception.Message, exception);
            }

            Request request = null;

            if (requests.ContainsKey(_frameManager.MainFrame.Url))
            {
                request = requests[_frameManager.MainFrame.Url];
            }

            return request?.Response;
        }

        public async Task<Stream> PdfAsync() => await PdfAsync(new PdfOptions());

        public async Task<Stream> PdfAsync(PdfOptions options)
        {
            var paperWidth = 8.5m;
            var paperHeight = 11m;

            if (!string.IsNullOrEmpty(options.Format))
            {
                if (!_paperFormats.ContainsKey(options.Format.ToLower()))
                {
                    throw new ArgumentException("Unknown paper format");
                }

                var format = _paperFormats[options.Format.ToLower()];
                paperWidth = format.Width;
                paperHeight = format.Height;
            }
            else
            {
                if (options.Width != null)
                {
                    paperWidth = ConvertPrintParameterToInches(options.Width);
                }
                if (options.Height != null)
                {
                    paperHeight = ConvertPrintParameterToInches(options.Height);
                }
            }

            var marginTop = ConvertPrintParameterToInches(options.MarginOptions.Top);
            var marginLeft = ConvertPrintParameterToInches(options.MarginOptions.Left);
            var marginBottom = ConvertPrintParameterToInches(options.MarginOptions.Bottom);
            var marginRight = ConvertPrintParameterToInches(options.MarginOptions.Right);

            JObject result = await _client.SendAsync("Page.printToPDF", new
            {
                landscape = options.Landscape,
                displayHeaderFooter = options.DisplayHeaderFooter,
                headerTemplate = options.HeaderTemplate,
                footerTemplate = options.FooterTemplate,
                printBackground = options.PrintBackground,
                scale = options.Scale,
                paperWidth,
                paperHeight,
                marginTop,
                marginBottom,
                marginLeft,
                marginRight,
                pageRanges = options.PageRanges
            });

            var buffer = Convert.FromBase64String(result.GetValue("data").Value<string>());

            if (!string.IsNullOrEmpty(options.Path))
            {
                using (var fs = new FileStream(options.Path, FileMode.Create, FileAccess.Write))
                {
                    fs.Write(buffer, 0, buffer.Length);
                }
            }

            return new MemoryStream(buffer);
        }

        #endregion

        #region Private Method

        private decimal ConvertPrintParameterToInches(object parameter)
        {
            if (parameter == null)
            {
                return 0;
            }

            var pixels = 0m;

            if (parameter is decimal || parameter is int)
            {
                pixels = Convert.ToDecimal(parameter);
            }
            else
            {
                var text = parameter.ToString();
                var unit = text.Substring(text.Length - 2).ToLower();
                var valueText = "";

                if (_unitToPixels.ContainsKey(unit))
                {
                    valueText = text.Substring(0, text.Length - 2);
                }
                else
                {
                    // In case of unknown unit try to parse the whole parameter as number of pixels.
                    // This is consistent with phantom's paperSize behavior.
                    unit = "px";
                    valueText = text;
                }

                if (Decimal.TryParse(valueText, NumberStyles.Any, CultureInfo.InvariantCulture.NumberFormat, out var number))
                {
                    pixels = number * _unitToPixels[unit];
                }
                else
                {
                    throw new ArgumentException($"Failed to parse parameter value: '{text}'", nameof(parameter));
                }
            }

            return pixels / 96;
        }

        private async void client_MessageReceived(object sender, MessageEventArgs e)
        {
            switch (e.MessageID)
            {
                case "Page.loadEventFired":
                    Load?.Invoke(this, new EventArgs());
                    break;
                case "Runtime.consoleAPICalled":
                    OnConsoleAPI(e);
                    break;
                case "Page.javascriptDialogOpening":
                    OnDialog(e);
                    break;
                case "Runtime.exceptionThrown":
                    HandleException(e.MessageData.exception.exceptionDetails);
                    break;
                case "Security.certificateError":
                    await OnCertificateError(e);
                    break;
                case "Inspector.targetCrashed":
                    OnTargetCrashed();
                    break;
                case "Performance.metrics":
                    EmitMetrics(e);
                    break;
            }
        }

        private void OnTargetCrashed()
        {
            if (Error == null)
            {
                throw new TargetCrashedException();
            }

            Error.Invoke(this, new ErrorEventArgs());
        }

        private void EmitMetrics(MessageEventArgs e)
        {
            throw new NotImplementedException();
        }

        private async Task OnCertificateError(MessageEventArgs e)
        {
            if (_ignoreHTTPSErrors)
            {
                //TODO: Puppeteer is silencing an error here, I don't know if that's necessary here
                await _client.SendAsync("Security.handleCertificateError", new Dictionary<string, object>
                {
                    {"eventId", e.MessageData.eventId },
                    {"action", "continue"}
                });

            }
        }

        private void HandleException(string exceptionDetails)
        {
            throw new NotImplementedException();
        }

        private void OnDialog(MessageEventArgs e)
        {
            throw new NotImplementedException();
        }

        private void OnConsoleAPI(MessageEventArgs e)
        {
            throw new NotImplementedException();
        }

        private async Task SetViewport(ViewPortOptions viewport)
        {
            var needsReload = await _emulationManager.EmulateViewport(_client, viewport);
            _viewport = viewport;
            if (needsReload)
            {
                await Reload();
            }
        }

        private Task Reload()
        {
            throw new NotImplementedException();
        }


        private async Task Navigate(Session client, string url, string referrer)
        {

            dynamic response = await client.SendAsync("Page.navigate", new
            {
                url,
                referrer = referrer ?? string.Empty
            });

            if (response.errorText != null)
            {
                throw new NavigationException(response.errorText);
            }
        }

        #endregion

    }
}