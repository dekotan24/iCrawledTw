using Microsoft.Toolkit.Uwp.Notifications;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace iCrawledTw
{
	class Program
	{
		private static readonly string LastTweetIdsFile = "last_tweet_ids.json";
		private static readonly string CompletedUsersFile = "completed_users.json";
		private static readonly string SettingsFile = "settings.json";
		private static readonly string MediaUrlsFile = "media_urls.json";
		private static readonly string ChromeDriverPath = Path.Combine(Environment.CurrentDirectory, "chromedriver-win64", "chromedriver.exe");
		private static readonly string ManualDriversFolder = Path.Combine(Environment.CurrentDirectory, "chromedriver-win64");
		private static readonly string LogFile = "execution_log.txt";

		public enum MsgType
		{
			None,
			Info,
			Warning,
			Error,
			Success
		}

		public class Settings
		{
			public bool DownloadMedia { get; set; } = true;
			public string DownloadFolder { get; set; } = Path.Combine(Environment.CurrentDirectory, "Downloads");
			public string DownloadPathFormat { get; set; } = "{username}\\{tweetid}";
			public int RequestIntervalMs { get; set; } = 5000;
			public bool UseRandomInterval { get; set; } = true;
			public int RandomIntervalMinMs { get; set; } = 3000;
			public int RandomIntervalMaxMs { get; set; } = 7000;
			public int MaxScrolls { get; set; } = 0;
			public int ExecutionIntervalMs { get; set; } = 300000;
			public string TwitterUsername { get; set; } = "";
			public string TwitterPassword { get; set; } = "";
			public string ProxyServer { get; set; } = "";
			public string MediaFilter { get; set; } = "all";
			public bool EnableLogging { get; set; } = false;
			public bool RandomizeUserAgent { get; set; } = true;
			public int ScrollDelayIncreaseMs { get; set; } = 1000;
			public int SaveIntervalTweets { get; set; } = 50;
			public bool DownloadDuringCrawl { get; set; } = true;
			public bool OverwriteOnRecrawl { get; set; } = false;
			public bool EnableToastNotifications { get; set; } = true;
			public bool EnableDiscordNotifications { get; set; } = false;
			public string DiscordWebhookUrl { get; set; } = "";
		}

		static void Main(string[] args)
		{
			var settings = LoadSettings();
			IWebDriver driver = null;
			bool isLoggedIn = false;

			while (true)
			{
				Console.Clear();
				SetMsg("処理を選択してください（数値を入力）：");
				SetMsg("1: メディアURL解析（通常/再クロール）");
				SetMsg("2: 設定画面");
				SetMsg("3: キャッシュクリア");
				SetMsg("4: メディアURLをCSVにエクスポート");
				SetMsg("5: 終了");
				string choice = Console.ReadLine();

				switch (choice)
				{
					case "1":
						Console.Clear();
						SetMsg("初期化処理中です。しばらくお待ちください…", true, MsgType.Info, ConsoleColor.Magenta);
						if (driver == null)
						{
							string chromeVersion = GetChromeVersion();
							SetMsg($"検出されたChromeバージョン: {chromeVersion}", true, MsgType.Info);
							try
							{
								UpdateChromeDriver(chromeVersion);
							}
							catch (Exception ex)
							{
								SetMsg($"ChromeDriverの取得に失敗しました: {ex.Message}", true, MsgType.Warning, ConsoleColor.Magenta);
								SetMsg($"手動で {ManualDriversFolder} にchromedriver.exeを配置してください。\n配置後、Enterキーを押して続行...");
								Directory.CreateDirectory(ManualDriversFolder);
								Console.ReadLine();
								if (!File.Exists(Path.Combine(ManualDriversFolder, "chromedriver.exe")))
								{
									SetMsg("chromedriver.exeが見つかりません。終了します。", true, MsgType.Error, ConsoleColor.Red);
									return;
								}
								File.Copy(Path.Combine(ManualDriversFolder, "chromedriver.exe"), ChromeDriverPath, true);
							}

							ChromeOptions options = new ChromeOptions();
							options.AddArgument("--log-level=3");
							if (!string.IsNullOrEmpty(settings.ProxyServer))
								options.AddArgument($"--proxy-server={settings.ProxyServer}");
							if (settings.RandomizeUserAgent)
							{
								string[] userAgents = {
							"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36",
							"Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.114 Safari/537.36",
							"Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.101 Safari/537.36"
						};
								string randomUA = userAgents[new Random().Next(userAgents.Length)];
								options.AddArgument($"--user-agent={randomUA}");
								Log(settings, $"UserAgent: {randomUA}");
							}

							driver = new ChromeDriver(Path.GetDirectoryName(ChromeDriverPath), options);

							if (SessionManager.LoadSession(driver))
							{
								isLoggedIn = true;
							}
							else
							{
								PerformLogin(driver, settings.TwitterUsername, settings.TwitterPassword);
								SessionManager.SaveSession(driver);
								isLoggedIn = true;
							}
						}
						ProcessMediaExtraction(settings, driver, isLoggedIn);
						break;
					case "2":
						UpdateSettings(settings);
						SaveSettings(settings);
						break;
					case "3":
						ClearCache(settings);
						SessionManager.ClearSession();
						break;
					case "4":
						ExportMediaUrlsToCsv(settings);
						break;
					case "5":
						if (driver != null)
						{
							driver.Quit();
							driver.Dispose();
						}
						return;
					default:
						SetMsg("無効な入力です。1-5の数値を入力してください。", true, MsgType.Warning, ConsoleColor.Magenta);
						break;
				}
				SetMsg("\n続行するには何かキーを押してください...");
				Console.ReadKey();
			}
		}

		static void ProcessMediaExtraction(Settings settings, IWebDriver driver, bool isLoggedIn)
		{
			SetMsg("========================================");
			SetMsg("XのURLを入力してください（例: https://x.com/username または https://x.com/username/status/123456789）：");
			string inputUrl = Console.ReadLine();
			SetMsg("再クロールしますか？（1: はい, 2: いいえ）");
			bool isRecrawl = Console.ReadLine() == "1";
			Log(settings, $"処理開始: {inputUrl} (再クロール: {isRecrawl})");

			var lastTweetIds = LoadLastTweetIds();
			var completedUsers = LoadCompletedUsers();
			var mediaUrls = LoadMediaUrls();

			try
			{
				driver.Navigate().GoToUrl(inputUrl);
				Thread.Sleep(5000);

				List<(string Url, string TweetId)> mediaUrlsWithTweetIds = new List<(string Url, string TweetId)>();
				string latestTweetId = null;

				if (inputUrl.Contains("/status/"))
				{
					SetMsg("\n指定ツイートのメディアを抽出中...", true, MsgType.Info);
					try
					{
						string twId = ExtractTweetIdFromUrl(inputUrl);
						mediaUrlsWithTweetIds = ExtractMediaUrlsFromTweet(driver, settings, twId).Select(url => (url.Url, url.TweetId)).ToList();
						Log(settings, $"抽出されたメディアURL数: {mediaUrlsWithTweetIds.Count}");
						foreach (var media in mediaUrlsWithTweetIds)
						{
							Log(settings, $"取得: {media.Url}");
						}
						UpdateMediaUrls(mediaUrls, mediaUrlsWithTweetIds);
					}
					catch (Exception ex)
					{
						SetMsg($"ツイートメディア抽出エラー: {ex.Message}", true, MsgType.Error, ConsoleColor.Red);
						Log(settings, $"ツイートメディア抽出エラー: {ex.StackTrace}");
						throw;
					}
				}
				else
				{
					string username = ExtractUsernameFromUrl(inputUrl);
					string lastTweetId = lastTweetIds.ContainsKey(username) ? lastTweetIds[username] : null;
					bool isCompleted = completedUsers.ContainsKey(username) && completedUsers[username] && !isRecrawl;
					SetMsg($"\n{username} のメディアを含むツイートを収集中（前回の最新ツイートID: {lastTweetId ?? "なし"}, 完了済み: {isCompleted}）...", true, MsgType.Info);
					mediaUrlsWithTweetIds = ExtractMediaUrlsFromUserProfile(driver, isCompleted ? lastTweetId : null, out latestTweetId, settings, out bool reachedEnd, mediaUrls, username);

					if (!string.IsNullOrEmpty(latestTweetId))
					{
						lastTweetIds[username] = latestTweetId;
						SaveLastTweetIds(lastTweetIds);
					}
					if (reachedEnd && !isCompleted)
					{
						completedUsers[username] = true;
						SaveCompletedUsers(completedUsers);
						Log(settings, $"{username} の全メディアをクロール完了");
					}
				}

				if (mediaUrlsWithTweetIds.Any())
				{
					string username = ExtractUsernameFromUrl(inputUrl);
					if (settings.DownloadMedia && settings.DownloadDuringCrawl)
					{
						DownloadMediaFiles(mediaUrlsWithTweetIds, username, settings, isRecrawl, driver);
					}
					SetMsg("\n取得したメディアURL:", true, MsgType.Info);
					int index = 1;
					foreach (var media in mediaUrlsWithTweetIds)
					{
						SetMsg($"{media.Url}");
						Log(settings, $"{index}. {media.Url}");
						index++;
					}
					Notify(settings, "処理完了", $"{username} のメディア抽出が完了しました。取得件数: {mediaUrlsWithTweetIds.Count}");
				}
				else
				{
					SetMsg("\n警告: メディアURLが見つかりませんでした。ページの読み込みや設定を確認してください。", true, MsgType.Error, ConsoleColor.Red);
					Log(settings, "メディアが見つかりませんでした。");
					Notify(settings, "処理完了", "メディアが見つかりませんでした。");
				}
			}
			catch (Exception ex)
			{
				SetMsg($"エラー: {ex.Message}", true, MsgType.Error, ConsoleColor.Red);
				Log(settings, $"エラー: {ex.StackTrace}");
				Notify(settings, "エラー", $"処理中にエラーが発生しました: {ex.Message}");
			}
		}

		static string GetChromeVersion()
		{
			try
			{
				using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Google\Chrome") ??
								 Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Google\Chrome"))
				{
					if (key != null)
					{
						string version = key.GetValue("Version")?.ToString();
						if (!string.IsNullOrEmpty(version)) return version;
					}
				}

				string chromePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
				if (File.Exists(chromePath))
				{
					var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(chromePath);
					return versionInfo.FileVersion;
				}

				throw new Exception("Chromeが見つかりませんでした。");
			}
			catch (Exception ex)
			{
				throw new Exception($"Chromeバージョンの取得に失敗しました: {ex.Message}");
			}
		}

		static void UpdateChromeDriver(string chromeVersion)
		{
			if (File.Exists(ChromeDriverPath))
			{
				string currentDriverVersion = GetChromeDriverVersion();
				SetMsg($"ChromeDriverのバージョン: {currentDriverVersion}", true, MsgType.Info);
				if (currentDriverVersion == chromeVersion)
				{
					SetMsg("ChromeDriverは最新です。", true, MsgType.Info, ConsoleColor.Green);
					return;
				}
				File.Delete(ChromeDriverPath);
			}

			string downloadUrl = $"https://storage.googleapis.com/chrome-for-testing-public/{chromeVersion}/win64/chromedriver-win64.zip";
			SetMsg($"ChromeDriverのダウンロードURL: {downloadUrl}", true, MsgType.Info);
			DownloadAndExtractChromeDriver(downloadUrl);
			SetMsg("ChromeDriverを更新しました。", true, MsgType.Info);
		}

		static string GetChromeDriverVersion()
		{
			if (!File.Exists(ChromeDriverPath)) return null;
			var process = new System.Diagnostics.Process
			{
				StartInfo = new System.Diagnostics.ProcessStartInfo
				{
					FileName = ChromeDriverPath,
					Arguments = "--version",
					RedirectStandardOutput = true,
					UseShellExecute = false,
					CreateNoWindow = true
				}
			};
			process.Start();
			string output = process.StandardOutput.ReadToEnd();
			process.WaitForExit();
			return output.Split(' ')[1];
		}

		static void DownloadAndExtractChromeDriver(string url)
		{
			using (var client = new HttpClient())
			{
				var zipBytes = client.GetByteArrayAsync(url).Result;
				File.WriteAllBytes("chromedriver.zip", zipBytes);
			}

			System.IO.Compression.ZipFile.ExtractToDirectory("chromedriver.zip", Environment.CurrentDirectory, true);
			File.Delete("chromedriver.zip");
		}

		static void PerformLogin(IWebDriver driver, string username, string password)
		{
			if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
			{
				SetMsg("ログイン情報が設定されていません。公開データのみを取得します。", true, MsgType.Warning, ConsoleColor.Magenta);
				return;
			}
			try
			{
				var loginField = driver.FindElement(By.Name("text"));
				loginField.SendKeys(username);
				driver.FindElement(By.XPath("//span[contains(text(),'次へ')]")).Click();
				Thread.Sleep(1000);
				var passwordField = driver.FindElement(By.Name("password"));
				passwordField.SendKeys(password);
				driver.FindElement(By.XPath("//span[contains(text(),'ログイン')]")).Click();
				Thread.Sleep(3000);
				SetMsg("ログインに成功しました。", true, MsgType.Info);
			}
			catch (NoSuchElementException)
			{
				SetMsg("ログイン不要またはログイン画面が表示されませんでした。", true, MsgType.Warning, ConsoleColor.Magenta);
			}
		}

		static List<(string Url, string TweetId)> ExtractMediaUrlsFromTweet(IWebDriver driver, Settings settings, string tweetId)
		{
			var mediaUrlsWithTweetIds = new List<(string Url, string TweetId)>();
			try
			{
				WebDriverWait wait = new WebDriverWait(driver, TimeSpan.FromSeconds(10));
				wait.Until(d => d.FindElements(By.TagName("video")).Count > 0 || d.FindElements(By.TagName("img")).Count > 0);

				if (settings.MediaFilter == "all" || settings.MediaFilter == "images")
				{
					var imageElements = driver.FindElements(By.TagName("img"));
					foreach (var img in imageElements)
					{
						string src = img.GetAttribute("src");
						if (src != null && src.Contains("media") && !mediaUrlsWithTweetIds.Any(m => m.Url == src))
						{
							src = Regex.Replace(src, @"&name=[^&]*", "");
							mediaUrlsWithTweetIds.Add((Url: src, TweetId: tweetId));
						}
					}
				}

				if (settings.MediaFilter == "all" || settings.MediaFilter == "videos")
				{
					var videoElements = driver.FindElements(By.TagName("video"));
					foreach (var video in videoElements)
					{
						string src = video.GetAttribute("src");
						if (!string.IsNullOrEmpty(src))
						{
							if (src.StartsWith("blob:"))
							{
								string realVideoUrl = GetRealVideoUrlFromBlob(driver, src, settings);
								if (!string.IsNullOrEmpty(realVideoUrl) && !mediaUrlsWithTweetIds.Any(m => m.Url == realVideoUrl))
								{
									mediaUrlsWithTweetIds.Add((Url: realVideoUrl, TweetId: tweetId));
								}
								else
								{
									SetMsg($"警告: blob URL {src} を実際のURLに変換できませんでした。", true, MsgType.Warning, ConsoleColor.Magenta);
									Log(settings, $"警告: blob URL {src} を実際のURLに変換できませんでした。");
								}
							}
							else if (!mediaUrlsWithTweetIds.Any(m => m.Url == src))
							{
								src = Regex.Replace(src, @"&name=[^&]*", "");
								mediaUrlsWithTweetIds.Add((Url: src, TweetId: tweetId));
							}
						}

						var sources = video.FindElements(By.TagName("source"));
						foreach (var source in sources)
						{
							string videoSrc = source.GetAttribute("src");
							if (!string.IsNullOrEmpty(videoSrc) && !mediaUrlsWithTweetIds.Any(m => m.Url == videoSrc))
							{
								mediaUrlsWithTweetIds.Add((Url: videoSrc, TweetId: tweetId));
							}
						}
					}
				}
			}
			catch (Exception ex)
			{
				SetMsg($"メディア抽出エラー: {ex.Message}", true, MsgType.Error, ConsoleColor.Red);
				Log(settings, $"メディア抽出エラー: {ex.StackTrace}");
				throw;
			}
			return mediaUrlsWithTweetIds;
		}

		static string GetRealVideoUrlFromBlob(IWebDriver driver, string blobUrl, Settings settings, bool forcem3u8 = false)
		{
			if (driver == null)
			{
				SetMsg("エラー: IWebDriverがnullです。", true, MsgType.Error, ConsoleColor.Red);
				Log(settings, "エラー: IWebDriverがnullです。");
				return null;
			}

			// video要素を取得
			// ネットワークリクエストから.mp4を優先的に取得、次に.m3u8
			try
			{
				WebDriverWait wait = new WebDriverWait(driver, TimeSpan.FromSeconds(15));
				wait.Until(d => ((IJavaScriptExecutor)d).ExecuteScript("return document.readyState").Equals("complete"));
				wait.Until(d => ((IJavaScriptExecutor)d).ExecuteScript("return performance.getEntriesByType('resource').filter(r => r.name.includes('video.twimg.com')).length > 0"));

				string script = @"
            var video = document.querySelector('video[src=""" + blobUrl.Replace("\"", "\\\"") + @"""]');
            if (video) {
                if (video.currentSrc && !video.currentSrc.startsWith('blob:')) {
                    return video.currentSrc;
                }
                var source = video.querySelector('source');
                if (source && source.getAttribute('src')) {
                    return source.getAttribute('src');
                }
            }
            var resources = performance.getEntriesByType('resource').filter(r => 
                r.name.includes('video.twimg.com'));
            var mp4Resource = resources.find(r => r.name.endsWith('.mp4'));
            if (mp4Resource) return mp4Resource.name;
            var m3u8Resource = resources.find(r => r.name.endsWith('.m3u8'));
            if (m3u8Resource) return m3u8Resource.name;
            return null;
        ";
				string script2 = @"
            var video = document.querySelector('video[src=""" + blobUrl.Replace("\"", "\\\"") + @"""]');
            if (video) {
                if (video.currentSrc && !video.currentSrc.startsWith('blob:')) {
                    return video.currentSrc;
                }
                var source = video.querySelector('source');
                if (source && source.getAttribute('src')) {
                    return source.getAttribute('src');
                }
            }
            var resources = performance.getEntriesByType('resource').filter(r => 
                r.name.includes('video.twimg.com'));
            var m3u8Resource = resources.find(r => r.name.endsWith('.m3u8'));
            if (m3u8Resource) return m3u8Resource.name;
            var mp4Resource = resources.find(r => r.name.endsWith('.mp4'));
            if (mp4Resource) return mp4Resource.name;
            return null;
        ";
				var result = new string("");
				if (forcem3u8)
				{
					result = (string)((IJavaScriptExecutor)driver).ExecuteScript(script2);
				}
				else
				{
					result = (string)((IJavaScriptExecutor)driver).ExecuteScript(script);
				}
				if (!string.IsNullOrEmpty(result))
				{
					SetMsg($"Blobから取得したURL: {result}", true, MsgType.Info);
					Log(settings, $"Blobから取得したURL: {result}");
					return result;
				}

				SetMsg($"警告: Blobから実際のURLを取得できませんでした: {blobUrl}", true, MsgType.Warning, ConsoleColor.Magenta);
				Log(settings, $"警告: Blobから実際のURLを取得できませんでした: {blobUrl}");

				var allResources = ((IJavaScriptExecutor)driver).ExecuteScript("return performance.getEntriesByType('resource');");
				SetMsg($"利用可能なリソース: {JsonSerializer.Serialize(allResources)}", true, MsgType.Info);
				Log(settings, $"利用可能なリソース: {JsonSerializer.Serialize(allResources)}");

				return null;
			}
			catch (Exception ex)
			{
				SetMsg($"Blob URL変換エラー: {blobUrl} - {ex.Message}", true, MsgType.Error, ConsoleColor.Red);
				Log(settings, $"Blob URL変換エラー: {blobUrl} - {ex.StackTrace}");
				return null;
			}
		}

		static List<(string Url, string TweetId)> ExtractMediaUrlsFromUserProfile(IWebDriver driver, string lastTweetId, out string latestTweetId, Settings settings, out bool reachedEnd, Dictionary<string, List<string>> mediaUrls, string username)
		{
			var mediaUrlsWithTweetIds = new List<(string Url, string TweetId)>();
			latestTweetId = null;
			reachedEnd = false;
			int scrollCount = 0;
			int currentDelay = settings.RequestIntervalMs;
			int processedTweets = 0;
			var startTime = DateTime.Now;

			while (settings.MaxScrolls == 0 || scrollCount < settings.MaxScrolls)
			{
				if (CheckForCaptcha(driver))
				{
					SetMsg("CAPTCHAが検出されました。手動で解決してください。解決後、Enterキーを押して続行...", true, MsgType.Warning, ConsoleColor.Magenta);
					Notify(settings, "CAPTCHA検出", "CAPTCHAが表示されました。手動で解決してください。");
					Console.ReadLine();
				}

				long lastHeight = (long)((IJavaScriptExecutor)driver).ExecuteScript("return document.body.scrollHeight");

				var tweetElements = driver.FindElements(By.CssSelector("article[data-testid='tweet']"));
				foreach (var tweet in tweetElements)
				{
					string tweetId = tweet.GetAttribute("data-tweet-id") ?? GetTweetIdFromLink(tweet);
					if (string.IsNullOrEmpty(tweetId)) continue;

					if (latestTweetId == null) latestTweetId = tweetId;
					if (tweetId == lastTweetId) return mediaUrlsWithTweetIds;

					processedTweets++;
					var elapsed = DateTime.Now - startTime;
					SetMsg($"\r処理中: 経過時間 {elapsed.TotalSeconds:F1}秒, 処理ツイート数 {processedTweets}", true, MsgType.Info);

					var tweetMediaUrls = new List<string>();
					if (settings.MediaFilter == "all" || settings.MediaFilter == "images")
					{
						var images = tweet.FindElements(By.TagName("img"));
						foreach (var img in images)
						{
							string src = img.GetAttribute("src");
							if (src.Contains("media") && !mediaUrlsWithTweetIds.Any(m => m.Url == src))
							{
								src = Regex.Replace(src, @"&name=[^&]*", "");
								mediaUrlsWithTweetIds.Add((Url: src, TweetId: tweetId));
								tweetMediaUrls.Add(src);
								if (settings.DownloadDuringCrawl && settings.DownloadMedia)
									DownloadSingleMedia(src, tweetId, username, settings);
							}
						}
					}
					if (settings.MediaFilter == "all" || settings.MediaFilter == "videos")
					{
						var videos = tweet.FindElements(By.TagName("video"));
						foreach (var video in videos)
						{
							string src = video.GetAttribute("src");
							if (!string.IsNullOrEmpty(src))
							{
								if (src.StartsWith("blob:"))
								{
									string realVideoUrl = GetRealVideoUrlFromBlob(driver, src, settings);
									if (!string.IsNullOrEmpty(realVideoUrl) && !mediaUrlsWithTweetIds.Any(m => m.Url == realVideoUrl))
									{
										mediaUrlsWithTweetIds.Add((Url: realVideoUrl, TweetId: tweetId));
										tweetMediaUrls.Add(realVideoUrl);
										if (settings.DownloadDuringCrawl && settings.DownloadMedia)
											DownloadSingleMedia(realVideoUrl, tweetId, username, settings);
									}
								}
								else if (!mediaUrlsWithTweetIds.Any(m => m.Url == src))
								{
									src = Regex.Replace(src, @"&name=[^&]*", "");
									mediaUrlsWithTweetIds.Add((Url: src, TweetId: tweetId));
									tweetMediaUrls.Add(src);
									if (settings.DownloadDuringCrawl && settings.DownloadMedia)
										DownloadSingleMedia(src, tweetId, username, settings);
								}
							}
						}
					}
					if (tweetMediaUrls.Any())
					{
						mediaUrls[tweetId] = tweetMediaUrls;
					}

					if (processedTweets % settings.SaveIntervalTweets == 0)
					{
						SaveMediaUrls(mediaUrls);
						Log(settings, $"途中保存: {processedTweets}ツイート処理済み");
					}
				}
				Console.WriteLine();

				((IJavaScriptExecutor)driver).ExecuteScript("window.scrollTo(0, document.body.scrollHeight);");
				int sleepTime = settings.MaxScrolls == 0 ? currentDelay : GetSleepTime(settings);
				Thread.Sleep(sleepTime);
				long newHeight = (long)((IJavaScriptExecutor)driver).ExecuteScript("return document.body.scrollHeight");
				if (newHeight == lastHeight)
				{
					reachedEnd = true;
					SaveMediaUrls(mediaUrls);
					break;
				}

				scrollCount++;
				if (settings.MaxScrolls == 0) currentDelay += settings.ScrollDelayIncreaseMs;
			}
			return mediaUrlsWithTweetIds;
		}

		static void DownloadSingleMedia(string url, string tweetId, string username, Settings settings)
		{
			string filePath = Path.Combine(settings.DownloadFolder,
				settings.DownloadPathFormat.Replace("{username}", username).Replace("{tweetid}", tweetId),
				$"{Path.GetFileNameWithoutExtension(url.Split('?')[0])}{Path.GetExtension(url.Split('?')[0])}");
			if (!settings.OverwriteOnRecrawl && File.Exists(filePath)) return;

			Directory.CreateDirectory(Path.GetDirectoryName(filePath));
			using (var client = new HttpClient())
			{
				var bytes = client.GetByteArrayAsync(url).Result;
				File.WriteAllBytes(filePath, bytes);
				SetMsg($"ダウンロード完了: {filePath}", true, MsgType.Success);
				Log(settings, $"ダウンロード完了: {filePath}");
			}
		}

		static bool CheckForCaptcha(IWebDriver driver)
		{
			try
			{
				return driver.FindElements(By.Id("challenge-stage")).Count > 0 || driver.Title.Contains("CAPTCHA");
			}
			catch
			{
				return false;
			}
		}

		static int GetSleepTime(Settings settings)
		{
			if (settings.UseRandomInterval)
			{
				return new Random().Next(settings.RandomIntervalMinMs, settings.RandomIntervalMaxMs);
			}
			return settings.RequestIntervalMs;
		}

		static string ExtractUsernameFromUrl(string url)
		{
			var parts = url.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
			return parts.FirstOrDefault(p => !p.StartsWith("http") && p != "x.com" && p != "twitter.com" && p != "status") ?? "";
		}

		static string ExtractTweetIdFromUrl(string url)
		{
			var parts = url.Split('/');
			return parts.Last();
		}

		static string GetTweetIdFromLink(IWebElement tweet)
		{
			try
			{
				var link = tweet.FindElement(By.CssSelector("a[href*='/status/']"));
				var href = link.GetAttribute("href");
				return href.Split('/').Last();
			}
			catch
			{
				return null;
			}
		}

		static Dictionary<string, string> LoadLastTweetIds()
		{
			if (File.Exists(LastTweetIdsFile))
			{
				string json = File.ReadAllText(LastTweetIdsFile);
				return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
			}
			return new Dictionary<string, string>();
		}

		static void SaveLastTweetIds(Dictionary<string, string> lastTweetIds)
		{
			string json = JsonSerializer.Serialize(lastTweetIds, new JsonSerializerOptions { WriteIndented = true });
			File.WriteAllText(LastTweetIdsFile, json);
		}

		static Dictionary<string, bool> LoadCompletedUsers()
		{
			if (File.Exists(CompletedUsersFile))
			{
				string json = File.ReadAllText(CompletedUsersFile);
				return JsonSerializer.Deserialize<Dictionary<string, bool>>(json) ?? new Dictionary<string, bool>();
			}
			return new Dictionary<string, bool>();
		}

		static void SaveCompletedUsers(Dictionary<string, bool> completedUsers)
		{
			string json = JsonSerializer.Serialize(completedUsers, new JsonSerializerOptions { WriteIndented = true });
			File.WriteAllText(CompletedUsersFile, json);
		}

		static Dictionary<string, List<string>> LoadMediaUrls()
		{
			if (File.Exists(MediaUrlsFile))
			{
				string json = File.ReadAllText(MediaUrlsFile);
				return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json) ?? new Dictionary<string, List<string>>();
			}
			return new Dictionary<string, List<string>>();
		}

		static void SaveMediaUrls(Dictionary<string, List<string>> mediaUrls)
		{
			string json = JsonSerializer.Serialize(mediaUrls, new JsonSerializerOptions { WriteIndented = true });
			File.WriteAllText(MediaUrlsFile, json);
		}

		static void UpdateMediaUrls(Dictionary<string, List<string>> mediaUrls, List<(string Url, string TweetId)> newUrls)
		{
			foreach (var (url, tweetId) in newUrls)
			{
				if (!mediaUrls.ContainsKey(tweetId))
					mediaUrls[tweetId] = new List<string>();
				if (!mediaUrls[tweetId].Contains(url))
					mediaUrls[tweetId].Add(url);
			}
			SaveMediaUrls(mediaUrls);
		}

		static Settings LoadSettings()
		{
			if (File.Exists(SettingsFile))
			{
				string json = File.ReadAllText(SettingsFile);
				return JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
			}
			return new Settings();
		}

		static void SaveSettings(Settings settings)
		{
			string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
			File.WriteAllText(SettingsFile, json);
		}

		static void UpdateSettings(Settings settings)
		{
			while (true)
			{
				Console.Clear();
				SetMsg("設定グループを選択してください（数値を入力）：");
				SetMsg($"1: ダウンロード設定");
				SetMsg($"2: クロール設定");
				SetMsg($"3: アカウント設定");
				SetMsg($"4: 通知設定");
				SetMsg($"5: その他の設定");
				SetMsg($"0: 戻る");
				SetMsg($"\n現在の設定概要:\n\tダウンロード: {(settings.DownloadMedia ? "はい" : "いいえ")}\n\tフォルダ: {settings.DownloadFolder}\n\tMaxScrolls: {settings.MaxScrolls}");
				string groupChoice = Console.ReadLine();

				switch (groupChoice)
				{
					case "1":
						UpdateDownloadSettings(settings);
						break;
					case "2":
						UpdateCrawlSettings(settings);
						break;
					case "3":
						UpdateAccountSettings(settings);
						break;
					case "4":
						UpdateNotificationSettings(settings);
						break;
					case "5":
						UpdateOtherSettings(settings);
						break;
					case "0":
						return;
					default:
						SetMsg("無効な入力です。0-5の数値を入力してください。", true, MsgType.Warning, ConsoleColor.Magenta);
						break;
				}
			}
		}

		static void UpdateDownloadSettings(Settings settings)
		{
			while (true)
			{
				Console.Clear();
				SetMsg("ダウンロード設定を選択してください（数値を入力）：");
				SetMsg($"1: メディアをダウンロードするか [現在の値: {(settings.DownloadMedia ? "はい" : "いいえ")}]");
				SetMsg($"2: ダウンロードフォルダ [現在の値: {settings.DownloadFolder}]");
				SetMsg($"3: ダウンロードパス形式 [現在の値: {settings.DownloadPathFormat}]");
				SetMsg($"4: クロール中ダウンロード [現在の値: {(settings.DownloadDuringCrawl ? "はい" : "いいえ")}]");
				SetMsg($"5: 再クロール時上書き [現在の値: {(settings.OverwriteOnRecrawl ? "はい" : "いいえ")}]");
				SetMsg($"0: 戻る");
				string choice = Console.ReadLine();

				switch (choice)
				{
					case "1":
						SetMsg("メディアをダウンロードしますか？（1: はい, 2: いいえ）");
						settings.DownloadMedia = Console.ReadLine() == "1";
						break;
					case "2":
						SetMsg("新しいダウンロードフォルダを入力:");
						string newFolder = Console.ReadLine();
						if (!string.IsNullOrEmpty(newFolder)) settings.DownloadFolder = newFolder;
						break;
					case "3":
						SetMsg("新しいパス形式を入力（例: {username}\\{tweetid}）:");
						string newFormat = Console.ReadLine();
						if (!string.IsNullOrEmpty(newFormat)) settings.DownloadPathFormat = newFormat;
						break;
					case "4":
						SetMsg("クロール中にダウンロードしますか？（1: はい, 2: いいえ）");
						settings.DownloadDuringCrawl = Console.ReadLine() == "1";
						break;
					case "5":
						SetMsg("再クロール時に上書きしますか？（1: はい, 2: いいえ）");
						settings.OverwriteOnRecrawl = Console.ReadLine() == "1";
						break;
					case "0":
						return;
					default:
						SetMsg("無効な入力です。0-5の数値を入力してください。", true, MsgType.Warning, ConsoleColor.Magenta);
						break;
				}
				SetMsg("設定を更新しました。続けて変更しますか？（Enterで続行、任意のキーで戻る）");
				if (Console.ReadLine() != "") return;
			}
		}

		static void UpdateCrawlSettings(Settings settings)
		{
			while (true)
			{
				Console.Clear();
				SetMsg("クロール設定を選択してください（数値を入力）：");
				SetMsg($"1: リクエスト間隔（ミリ秒） [現在の値: {settings.RequestIntervalMs}]");
				SetMsg($"2: ランダム待機を使用するか [現在の値: {(settings.UseRandomInterval ? "はい" : "いいえ")}]");
				SetMsg($"3: ランダム待機の最小値（ミリ秒） [現在の値: {settings.RandomIntervalMinMs}]");
				SetMsg($"4: ランダム待機の最大値（ミリ秒） [現在の値: {settings.RandomIntervalMaxMs}]");
				SetMsg($"5: 最大スクロール回数（0で無制限） [現在の値: {settings.MaxScrolls}]");
				SetMsg($"6: 無制限時の待機増加（ミリ秒） [現在の値: {settings.ScrollDelayIncreaseMs}]");
				SetMsg($"7: 途中保存間隔（ツイート数） [現在の値: {settings.SaveIntervalTweets}]");
				SetMsg($"0: 戻る");
				string choice = Console.ReadLine();

				switch (choice)
				{
					case "1":
						SetMsg("新しいリクエスト間隔を入力（例: 5000）:");
						string newInterval = Console.ReadLine();
						if (int.TryParse(newInterval, out int interval)) settings.RequestIntervalMs = interval;
						break;
					case "2":
						SetMsg("ランダム待機を有効にしますか？（1: はい, 2: いいえ）");
						settings.UseRandomInterval = Console.ReadLine() == "1";
						break;
					case "3":
						SetMsg("新しい最小値を入力（例: 3000）:");
						string newMin = Console.ReadLine();
						if (int.TryParse(newMin, out int min)) settings.RandomIntervalMinMs = min;
						break;
					case "4":
						SetMsg("新しい最大値を入力（例: 7000）:");
						string newMax = Console.ReadLine();
						if (int.TryParse(newMax, out int max)) settings.RandomIntervalMaxMs = max;
						break;
					case "5":
						SetMsg("新しいスクロール回数を入力（例: 0）:");
						string newScrolls = Console.ReadLine();
						if (int.TryParse(newScrolls, out int scrolls)) settings.MaxScrolls = scrolls;
						break;
					case "6":
						SetMsg("新しい待機増加値を入力（例: 1000）:");
						string newDelayIncrease = Console.ReadLine();
						if (int.TryParse(newDelayIncrease, out int delayIncrease)) settings.ScrollDelayIncreaseMs = delayIncrease;
						break;
					case "7":
						SetMsg("新しい保存間隔を入力（例: 50）:");
						string newSaveInterval = Console.ReadLine();
						if (int.TryParse(newSaveInterval, out int saveInterval)) settings.SaveIntervalTweets = saveInterval;
						break;
					case "0":
						return;
					default:
						SetMsg("無効な入力です。0-7の数値を入力してください。", true, MsgType.Warning, ConsoleColor.Magenta);
						break;
				}
				SetMsg("設定を更新しました。続けて変更しますか？（Enterで続行、任意のキーで戻る）");
				if (Console.ReadLine() != "") return;
			}
		}

		static void UpdateAccountSettings(Settings settings)
		{
			while (true)
			{
				Console.Clear();
				SetMsg("アカウント設定を選択してください（数値を入力）：");
				SetMsg($"1: Twitterユーザー名 [現在の値: {settings.TwitterUsername}]");
				SetMsg($"2: Twitterパスワード [現在の値: {(string.IsNullOrEmpty(settings.TwitterPassword) ? "未設定" : "********")}]");
				SetMsg($"0: 戻る");
				string choice = Console.ReadLine();

				switch (choice)
				{
					case "1":
						SetMsg("新しいユーザー名を入力:");
						string newUsername = Console.ReadLine();
						if (!string.IsNullOrEmpty(newUsername)) settings.TwitterUsername = newUsername;
						break;
					case "2":
						SetMsg("新しいパスワードを入力:");
						string newPassword = Console.ReadLine();
						if (!string.IsNullOrEmpty(newPassword)) settings.TwitterPassword = newPassword;
						break;
					case "0":
						return;
					default:
						SetMsg("無効な入力です。0-2の数値を入力してください。", true, MsgType.Warning, ConsoleColor.Magenta);
						break;
				}
				SetMsg("設定を更新しました。続けて変更しますか？（Enterで続行、任意のキーで戻る）");
				if (Console.ReadLine() != "") return;
			}
		}

		static void UpdateNotificationSettings(Settings settings)
		{
			while (true)
			{
				Console.Clear();
				SetMsg("通知設定を選択してください（数値を入力）：");
				SetMsg($"1: Windowsトースト通知 [現在の値: {(settings.EnableToastNotifications ? "はい" : "いいえ")}]");
				SetMsg($"2: Discord通知 [現在の値: {(settings.EnableDiscordNotifications ? "はい" : "いいえ")}]");
				SetMsg($"3: Discord Webhook URL [現在の値: {settings.DiscordWebhookUrl}]");
				SetMsg($"0: 戻る");
				string choice = Console.ReadLine();

				switch (choice)
				{
					case "1":
						SetMsg("トースト通知を有効にしますか？（1: はい, 2: いいえ）");
						settings.EnableToastNotifications = Console.ReadLine() == "1";
						break;
					case "2":
						SetMsg("Discord通知を有効にしますか？（1: はい, 2: いいえ）");
						settings.EnableDiscordNotifications = Console.ReadLine() == "1";
						break;
					case "3":
						SetMsg("新しいWebhook URLを入力:");
						string newWebhook = Console.ReadLine();
						if (!string.IsNullOrEmpty(newWebhook)) settings.DiscordWebhookUrl = newWebhook;
						break;
					case "0":
						return;
					default:
						SetMsg("無効な入力です。0-3の数値を入力してください。", true, MsgType.Warning, ConsoleColor.Magenta);
						break;
				}
				SetMsg("設定を更新しました。続けて変更しますか？（Enterで続行、任意のキーで戻る）");
				if (Console.ReadLine() != "") return;
			}
		}

		static void UpdateOtherSettings(Settings settings)
		{
			while (true)
			{
				Console.Clear();
				SetMsg("その他の設定を選択してください（数値を入力）：");
				SetMsg($"1: メディアフィルタ [現在の値: {settings.MediaFilter}]");
				SetMsg($"2: ログ出力 [現在の値: {(settings.EnableLogging ? "はい" : "いいえ")}]");
				SetMsg($"3: プロキシサーバ [現在の値: {settings.ProxyServer}]");
				SetMsg($"4: UserAgentランダム化 [現在の値: {(settings.RandomizeUserAgent ? "はい" : "いいえ")}]");
				SetMsg($"5: 実行間隔（ミリ秒） [現在の値: {settings.ExecutionIntervalMs}]");
				SetMsg($"0: 戻る");
				string choice = Console.ReadLine();

				switch (choice)
				{
					case "1":
						SetMsg("メディアフィルタを選択（1: 全て, 2: 画像のみ, 3: 動画のみ）:");
						string filterChoice = Console.ReadLine();
						if (filterChoice == "1") settings.MediaFilter = "all";
						else if (filterChoice == "2") settings.MediaFilter = "images";
						else if (filterChoice == "3") settings.MediaFilter = "videos";
						break;
					case "2":
						SetMsg("ログ出力を有効にしますか？（1: はい, 2: いいえ）");
						settings.EnableLogging = Console.ReadLine() == "1";
						break;
					case "3":
						SetMsg("新しいプロキシサーバを入力（例: http://proxy:port）:");
						string newProxy = Console.ReadLine();
						if (!string.IsNullOrEmpty(newProxy)) settings.ProxyServer = newProxy;
						break;
					case "4":
						SetMsg("UserAgentランダム化を有効にしますか？（1: はい, 2: いいえ）");
						settings.RandomizeUserAgent = Console.ReadLine() == "1";
						break;
					case "5":
						SetMsg("新しい実行間隔を入力（例: 300000）:");
						string newExecInterval = Console.ReadLine();
						if (int.TryParse(newExecInterval, out int execInterval)) settings.ExecutionIntervalMs = execInterval;
						break;
					case "0":
						return;
					default:
						SetMsg("無効な入力です。0-5の数値を入力してください。", true, MsgType.Warning, ConsoleColor.Magenta);
						break;
				}
				SetMsg("設定を更新しました。続けて変更しますか？（Enterで続行、任意のキーで戻る）");
				if (Console.ReadLine() != "") return;
			}
		}

		static void ClearCache(Settings settings)
		{
			SetMsg("キャッシュをクリアしますか？（1: はい, 2: いいえ）\n※クロールした情報を削除します。（ダウンロードしたファイルは残ります）", true, MsgType.Warning, ConsoleColor.Magenta);
			if (Console.ReadLine() == "1")
			{
				if (File.Exists(LastTweetIdsFile)) File.Delete(LastTweetIdsFile);
				if (File.Exists(CompletedUsersFile)) File.Delete(CompletedUsersFile);
				if (File.Exists(MediaUrlsFile)) File.Delete(MediaUrlsFile);
				if (File.Exists(LogFile)) File.Delete(LogFile);
				SetMsg("キャッシュをクリアしました。", true, MsgType.Info);
				Log(settings, "キャッシュをクリアしました。");
				Notify(settings, "キャッシュクリア", "すべてのキャッシュファイルを削除しました。");
			}
		}

		static void ExportMediaUrlsToCsv(Settings settings)
		{
			var mediaUrls = LoadMediaUrls();
			if (!mediaUrls.Any())
			{
				SetMsg("エクスポートするメディアURLがありません。", true, MsgType.Warning, ConsoleColor.Magenta);
				return;
			}

			string csvPath = Path.Combine(Environment.CurrentDirectory, "media_urls_export.csv");
			using (var writer = new StreamWriter(csvPath))
			{
				writer.WriteLine("TweetID,MediaURL");
				foreach (var entry in mediaUrls)
				{
					foreach (var url in entry.Value)
					{
						writer.WriteLine($"\"{entry.Key}\",\"{url}\"");
					}
				}
			}
			SetMsg($"メディアURLを {csvPath} にエクスポートしました。", true, MsgType.Success, ConsoleColor.Green);
			Log(settings, $"メディアURLを {csvPath} にエクスポートしました。");
			Notify(settings, "エクスポート完了", $"メディアURLを {csvPath} にエクスポートしました。");
		}


		static void DownloadMediaFiles(List<(string Url, string TweetId)> mediaUrls, string username, Settings settings, bool isRecrawl, IWebDriver driver)
		{
			using (var handler = new HttpClientHandler())
			{
				// WebDriverのクッキーを取得
				var cookies = driver.Manage().Cookies.AllCookies;
				var cookieContainer = new System.Net.CookieContainer();
				foreach (var cookie in cookies)
				{
					cookieContainer.Add(new System.Net.Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain));
				}
				handler.CookieContainer = cookieContainer;

				using (var client = new HttpClient(handler))
				{
					var driver2 = driver;
					IJavaScriptExecutor jsExecutor = (IJavaScriptExecutor)driver2;
					client.DefaultRequestHeaders.UserAgent.ParseAdd(jsExecutor.ExecuteScript("return navigator.userAgent;").ToString());


					int index = 1;
					foreach (var media in mediaUrls)
					{
						string downloadUrl = media.Url;
						if (media.Url.StartsWith("blob:"))
						{
							SetMsg($"blob URL検出: {media.Url}、変換を試みます...", true, MsgType.Info);
							Log(settings, $"blob URL検出: {media.Url}");
							downloadUrl = GetRealVideoUrlFromBlob(driver, media.Url, settings);
							if (string.IsNullOrEmpty(downloadUrl))
							{
								SetMsg($"エラー: {media.Url} から実際のURLを取得できませんでした。スキップします。", true, MsgType.Warning, ConsoleColor.Magenta);
								Log(settings, $"エラー: {media.Url} から実際のURLを取得できませんでした。");
								continue;
							}
							SetMsg($"変換成功: {downloadUrl}", true, MsgType.Info);
							Log(settings, $"変換成功: {downloadUrl}");
						}
						string extension = string.Empty;

						if (media.Url.Contains("?"))
						{
							string[] urlParts = media.Url.Split('?');
							extension = downloadUrl.EndsWith(".m3u8") ? ".ts" : (urlParts.Length > 1 ? urlParts[1].Replace("format=", ".") : Path.GetExtension(urlParts[0]));
						}
						else
						{
							extension = downloadUrl.EndsWith(".m3u8") ? ".ts" : Path.GetExtension(downloadUrl);
						}

						if (string.IsNullOrEmpty(extension)) extension = ".ts";

						string filePath = Path.Combine(settings.DownloadFolder,
							settings.DownloadPathFormat.Replace("{username}", username).Replace("{tweetid}", media.TweetId),
							$"{index}{extension}");

						if (isRecrawl && !settings.OverwriteOnRecrawl && File.Exists(filePath))
						{
							SetMsg($"スキップ: {filePath} (既存ファイル)", true, MsgType.Info);
							Log(settings, $"スキップ: {filePath}");
						}
						else
						{
							Directory.CreateDirectory(Path.GetDirectoryName(filePath));
							try
							{
								if (downloadUrl.EndsWith(".m3u8"))
								{
									var process = new System.Diagnostics.Process
									{
										StartInfo = new System.Diagnostics.ProcessStartInfo
										{
											FileName = "ffmpeg",
											Arguments = $"-y -i \"{downloadUrl}\" -c copy \"{filePath}\"",
											RedirectStandardOutput = true,
											UseShellExecute = false
											// CreateNoWindow = true
										}
									};
									SetMsg("Start FFMPEG encode", true, MsgType.Info);
									process.Start();
									process.WaitForExit();
									SetMsg($"ダウンロード完了 (FFmpeg): {filePath}", true, MsgType.Info);
									Log(settings, $"ダウンロード完了 (FFmpeg): {filePath}");
								}
								else
								{
									var response = client.GetAsync(downloadUrl).Result;
									response.EnsureSuccessStatusCode();
									var contentLength = response.Content.Headers.ContentLength;
									SetMsg($"コンテンツサイズ: {contentLength ?? -1} バイト", true, MsgType.Info);
									Log(settings, $"コンテンツサイズ: {contentLength ?? -1} バイト");

									if (contentLength.HasValue && contentLength < 1024)
									{
										SetMsg("コンテンツサイズが小さすぎます。動画ファイルでない可能性があります。\nm3u8で再取得します。", true, MsgType.Warning, ConsoleColor.Magenta);
										throw new mp4DownloadFailedException();
									}

									var bytes = response.Content.ReadAsByteArrayAsync().Result;
									File.WriteAllBytes(filePath, bytes);
									SetMsg($"ダウンロード完了: {filePath} (サイズ: {bytes.Length} バイト)", true, MsgType.Info);
									Log(settings, $"ダウンロード完了: {filePath} (サイズ: {bytes.Length} バイト)");
								}
							}
							catch (mp4DownloadFailedException)
							{
								try
								{
									if (media.Url.StartsWith("blob:"))
									{
										SetMsg($"blob URL検出: {media.Url}、変換を試みます...", true, MsgType.Info);
										Log(settings, $"blob URL検出: {media.Url}");
										downloadUrl = GetRealVideoUrlFromBlob(driver, media.Url, settings, true);
										if (string.IsNullOrEmpty(downloadUrl))
										{
											SetMsg($"エラー: {media.Url} から実際のURLを取得できませんでした。スキップします。", true, MsgType.Warning, ConsoleColor.Magenta);
											Log(settings, $"エラー: {media.Url} から実際のURLを取得できませんでした。");
											continue;
										}
										SetMsg($"変換成功: {downloadUrl}", true, MsgType.Info);
										Log(settings, $"変換成功: {downloadUrl}");
										var process = new System.Diagnostics.Process
										{
											StartInfo = new System.Diagnostics.ProcessStartInfo
											{
												FileName = "ffmpeg",
												Arguments = $"-y -i \"{downloadUrl}\" -c copy \"{filePath}\"",
												RedirectStandardOutput = true,
												UseShellExecute = false
												// CreateNoWindow = true
											}
										};
										SetMsg("Start FFMPEG(2) encode", true, MsgType.Info);
										process.Start();
										process.WaitForExit();
										SetMsg($"ダウンロード完了 (FFmpeg(re)): {filePath}", true, MsgType.Info);
										Log(settings, $"ダウンロード完了 (FFmpeg(re)): {filePath}");
									}
								}
								catch (Exception ex)
								{
									SetMsg($"ダウンロードエラー: {downloadUrl} - {ex.Message}", true, MsgType.Error, ConsoleColor.Red);
									Log(settings, $"ダウンロードエラー: {downloadUrl} - {ex.StackTrace}");
								}
							}
							catch (Exception ex)
							{
								SetMsg($"ダウンロードエラー: {downloadUrl} - {ex.Message}", true, MsgType.Error, ConsoleColor.Red);
								Log(settings, $"ダウンロードエラー: {downloadUrl} - {ex.StackTrace}");
							}
						}
						index++;
						Thread.Sleep(GetSleepTime(settings));
					}
				}
			}
		}

		static void Notify(Settings settings, string title, string message)
		{
			if (settings.EnableToastNotifications)
			{
				new ToastContentBuilder()
					.AddText(title)
					.AddText(message)
					.Show();
			}
			if (settings.EnableDiscordNotifications && !string.IsNullOrEmpty(settings.DiscordWebhookUrl))
			{
				using (var client = new HttpClient())
				{
					var content = new StringContent(
						JsonSerializer.Serialize(new { content = $"**{title}**\n{message}" }),
						System.Text.Encoding.UTF8,
						"application/json"
					);
					client.PostAsync(settings.DiscordWebhookUrl, content).Wait();
				}
			}
		}

		static void Log(Settings settings, string message)
		{
			if (settings.EnableLogging)
			{
				string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}";
				File.AppendAllText(LogFile, logEntry + Environment.NewLine);
			}
		}

		public static void SetMsg(string msg, bool isLine = true, MsgType mt = MsgType.None, ConsoleColor color = ConsoleColor.Black)
		{
			string msgHeader = string.Empty;
			switch (mt)
			{
				case MsgType.Info:
					Console.ResetColor();
					msgHeader = "[INFO] ";
					break;
				case MsgType.Warning:
					Console.ForegroundColor = ConsoleColor.Magenta;
					msgHeader = "[WARN] ";
					break;
				case MsgType.Error:
					Console.ForegroundColor = ConsoleColor.Red;
					msgHeader = "[ERROR] ";
					break;
				case MsgType.Success:
				case MsgType.None:
				default:
					Console.ResetColor();
					break;
			}
			Console.Write(msgHeader);
			Console.ResetColor();
			if (color != ConsoleColor.Black)
			{
				Console.ForegroundColor = color;
			}
			if (isLine)
			{
				Console.WriteLine(msg);
			}
			else
			{
				Console.Write(msg);
			}
			Console.ResetColor();
		}
	}

	public class SessionManager
	{
		private static readonly string SessionFile = "session.json";

		public class SessionData
		{
			public List<Cookie>? Cookies { get; set; }
			public DateTime Expires { get; set; }
		}

		public static void SaveSession(IWebDriver driver)
		{
			var cookies = driver.Manage().Cookies.AllCookies.Select(c => new Cookie
			{
				Name = c.Name,
				Value = c.Value,
				Domain = c.Domain,
				Path = c.Path,
				Expiry = c.Expiry,
				Secure = c.Secure
			}).ToList();

			var sessionData = new SessionData
			{
				Cookies = cookies,
				Expires = DateTime.Now.AddDays(365)
			};

			File.WriteAllText(SessionFile, JsonSerializer.Serialize(sessionData, new JsonSerializerOptions { WriteIndented = true }));
		}

		public static bool LoadSession(IWebDriver driver)
		{
			if (!File.Exists(SessionFile)) return false;

			var json = File.ReadAllText(SessionFile);
			var sessionData = JsonSerializer.Deserialize<SessionData>(json);
			if (sessionData == null || DateTime.Now > sessionData.Expires) return false;

			driver.Navigate().GoToUrl("https://x.com");
			foreach (var cookie in sessionData.Cookies)
			{
				driver.Manage().Cookies.AddCookie(new OpenQA.Selenium.Cookie(
					cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.Expiry));
			}

			driver.Navigate().GoToUrl("https://x.com/home");
			Thread.Sleep(2000);
			if (!driver.Url.Contains("login"))
			{
				Program.SetMsg("前回のセッションを復元しました。ログインをスキップします。", true, Program.MsgType.Info, ConsoleColor.Green);
				return true;
			}
			return false;
		}

		public static void ClearSession()
		{
			if (File.Exists(SessionFile)) File.Delete(SessionFile);
		}
	}

	public class Cookie
	{
		public string? Name { get; set; }
		public string? Value { get; set; }
		public string? Domain { get; set; }
		public string? Path { get; set; }
		public DateTime? Expiry { get; set; }
		public bool? Secure { get; set; }
	}

}

