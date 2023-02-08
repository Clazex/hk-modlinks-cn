using System.Collections;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Xml;

Console.OutputEncoding = Encoding.UTF8;

string src = Environment.GetEnvironmentVariable("HK_MODLINKS_MIRROR_SRC")
	?? "https://raw.githubusercontent.com/hk-modding/modlinks/main/";

string urlBase = Environment.GetEnvironmentVariable("HK_MODLINKS_MIRROR_BASE_URL")
	?? "https://hk-modlinks.clazex.net/";

List<string> skipList = Environment.GetEnvironmentVariable("HK_MODLINKS_MIRROR_SKIP_URLS")?.Split('|').ToList()
	?? new();

long maxAllowedSize;
if (!long.TryParse(Environment.GetEnvironmentVariable("HK_MODLINKS_MIRROR_MAX_ALLOWED_SIZE"), out maxAllowedSize)) {
	maxAllowedSize = 512 * 1024 * 1024;
}

bool rebaseOnly = Environment.GetEnvironmentVariable("HK_MODLINKS_MIRROR_REBASE_ONLY") != null;

string rebaseFromUrl = Environment.GetEnvironmentVariable("HK_MODLINKS_MIRROR_REBASE_FROM_URL")
	?? "https://hk-modlinks.clazex.net/";

static IEnumerable<T> AsGeneric<T>(IEnumerable enumerable) where T : class {
	IEnumerator enumerator = enumerable.GetEnumerator();
	while (enumerator.MoveNext()) {
		yield return (enumerator.Current as T)!;
	}
}

HttpClient client = new();
List<Task> tasks = new();

try {
	Directory.Delete("dist", true);
} catch { }

try {
	Directory.Delete("temp", true);
} catch { }

Directory.CreateDirectory("dist");

if (!rebaseOnly) {
	Directory.CreateDirectory("dist/apis");
	Directory.CreateDirectory("dist/mods");
	Directory.CreateDirectory("temp");
}

List<string> downloadedFiles = new();

static int GetApproxSize(HttpContent self) {
	return checked((int) (self.Headers.ContentDisposition switch {
		ContentDispositionHeaderValue val when val.Size.HasValue => val.Size.Value,
		_ => self.Headers.ContentLength.GetValueOrDefault(0)
	}));
}

static string ToFileSizeString(long size) {
	int i = (int) Math.Log(size, 1024);
	return string.Format("{0:0.##} {1}", size / Math.Pow(1024, i), i switch {
		_ when i < 0 => throw new ArgumentOutOfRangeException(nameof(size)),
		0 => "B",
		1 => "KiB",
		2 => "MiB",
		_ => "GiB",
	});
}

#region Rebase Only

if (rebaseOnly) {
	if (string.IsNullOrWhiteSpace(rebaseFromUrl)) {
		throw new InvalidOperationException("Rebase from URL not specified");
	}

	try {
		File.WriteAllText("dist/revision.txt", await client.GetStringAsync($"{src}revision.txt"));
	} catch (AggregateException e) when (e.InnerException is HttpRequestException) {
		throw new InvalidOperationException("Source is not a valid mirror");
	}

	File.WriteAllText(
		"dist/ApiLinks.xml",
		await client.GetStringAsync($"{src}/ApiLinks.xml")
			.ContinueWith(task => task.Result.Replace(rebaseFromUrl, urlBase))
	);
	File.WriteAllText(
		"dist/ModLinks.xml",
		await client.GetStringAsync($"{src}/ModLinks.xml")
			.ContinueWith(task => task.Result.Replace(rebaseFromUrl, urlBase))
	);

	return;
}

#endregion

#region Download and parse ApiLinks.xml

XmlDocument apiLinksXml = new() {
	PreserveWhitespace = true
};
apiLinksXml.Load(
	await client.GetStreamAsync($"{src}ApiLinks.xml")
);

#endregion

#region Download and parse ModLinks.xml

XmlDocument modLinksXml = new() {
	PreserveWhitespace = true
};
modLinksXml.Load(
	await client.GetStreamAsync($"{src}ModLinks.xml")
);

#endregion

#region Download Apis

string apiVersion = apiLinksXml.GetElementsByTagName("Version")[0]!.InnerText;
XmlNode apiLinksNode = apiLinksXml.GetElementsByTagName("Links")[0]!;

IEnumerable<Task> apiDownloadTasks = new XmlNode[] {
		apiLinksNode["Linux"]!.ChildNodes[1]!,
		apiLinksNode["Mac"]!.ChildNodes[1]!,
		apiLinksNode["Windows"]!.ChildNodes[1]!
	}
	.Select(node => client
		.GetAsync(node.InnerText)
		.ContinueWith(task => {
			node.InnerText = $"{urlBase}apis/{node.ParentNode!.Name}.zip";
			HttpContent content = task.Result.EnsureSuccessStatusCode().Content;

			string filePath = $"dist/apis/{node.ParentNode.Name}-{apiVersion}.zip";
			downloadedFiles.Add(filePath);

			using FileStream fileStream = File.Create(filePath, GetApproxSize(content));
			using Stream resStream = content.ReadAsStream();
			resStream.CopyTo(fileStream);

			long size = new FileInfo(filePath).Length;

			Console.WriteLine($"Downloaded {node.ParentNode.Name} api - {ToFileSizeString(size)}");
		})
	);

tasks.AddRange(apiDownloadTasks);

#if DEBUG
await Task.WhenAll(tasks);
tasks.Clear();
#endif

#endregion

#region Download mods

foreach (XmlNode modInfo in modLinksXml.GetElementsByTagName("Manifest")) {
	string name = modInfo["Name"]!.InnerText;

	XmlNode linkNode = AsGeneric<XmlNode>(modInfo["Link"]!.ChildNodes)
		.First((node) => node.Name == "#cdata-section");

	string link = linkNode.InnerText;

	if (skipList.Exists(link.Contains)) {
		continue;
	}

	string modName = Regex.Replace(name.Normalize(NormalizationForm.FormD), @"[^ -&(-~]", "");
	modName = Regex.Matches(modName, @"(?:[A-Z]?[a-z]+)|[A-Z]|\d+")
		.Select(match => match.Value)
		.Aggregate(new StringBuilder(modName.Length), (sb, part) => {
			_ = sb.Append(char.ToUpperInvariant(part[0]));

			if (part.Length > 1) {
				_ = sb.Append(part[1..]);
			}

			return sb;
		})
		.ToString();

	modName = modName.Length switch {
		1 => modName.ToUpperInvariant(),
		_ => char.ToUpperInvariant(modName[0]) + modName[1..]
	};

	string version = modInfo["Version"]!.InnerText;
	string fullModName = $"{modName}-v{version}";
	string fileName = fullModName + ".zip";

	Task downloadModtask = client
		.GetAsync(link)
		.ContinueWith(task => {
			HttpContent content = task.Result.EnsureSuccessStatusCode().Content;

			using MemoryStream ms = new(GetApproxSize(content));
			using (Stream resStream = content.ReadAsStream()) {
				resStream.CopyTo(ms);
			}

			long size = ms.Length;

			try {
				_ = new ZipArchive(ms, ZipArchiveMode.Read, true);

				string filePath = $"dist/mods/{fileName}";
				downloadedFiles.Add(filePath);

				ms.Position = 0;
				using FileStream modFile = File.Create(filePath, checked((int) size));
				ms.CopyTo(modFile);
			} catch (InvalidDataException) {
				_ = Directory.CreateDirectory($"temp/{modName}");

				string filePath = $"temp/{modName}/{modName}.dll";
				downloadedFiles.Add(filePath);

				using (FileStream tempFile = File.Create(filePath, checked((int) size))) {
					ms.CopyTo(tempFile);
				}

				ZipFile.CreateFromDirectory(
					$"temp/{modName}",
					$"dist/mods/{fileName}",
					CompressionLevel.SmallestSize,
					false
				);

				size = new FileInfo($"dist/mods/{fileName}").Length;

				Console.WriteLine($"Compressed {name}");
			}

			if (new FileInfo($"dist/mods/{fileName}").Length > maxAllowedSize) {
				Console.WriteLine($"Skipped {fullModName}");
			} else {
				linkNode.InnerText = urlBase + $"mods/{HttpUtility.HtmlEncode(fileName)}";

				if (name == modName) {
					Console.WriteLine($"Downloaded {fullModName} - {ToFileSizeString(size)}");
				} else {
					Console.WriteLine($"Downloaded {name} as {fullModName} - {ToFileSizeString(size)}");
				}
			}
		});

#if DEBUG
	if (tasks.Count > 2) {
		await Task.WhenAll(tasks);
		tasks.Clear();
	}
#endif

	tasks.Add(downloadModtask);
}

#endregion

await Task.WhenAll(tasks);

apiLinksXml.Save("dist/ApiLinks.xml");
modLinksXml.Save("dist/ModLinks.xml");

downloadedFiles.Sort();
using (SHA1 sha1 = SHA1.Create()) {
	using (CryptoStream hashStream = new(Stream.Null, sha1, CryptoStreamMode.Write)) {
		foreach (string path in downloadedFiles) {
			using FileStream file = File.OpenRead(path);
			file.CopyTo(hashStream);
		}
	}

	File.WriteAllText("dist/revision.txt", Convert.ToHexString(sha1.Hash!) + '\n');
}

#if !DEBUG
Directory.Delete("temp", true);
#endif
