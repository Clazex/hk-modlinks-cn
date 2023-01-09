using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Xml;

string urlBase = Environment.GetEnvironmentVariable("HK_MODLINKS_MIRROR_BASE_URL")
	?? "https://hk-modlinks.clazex.net/";

List<string> skipList = Environment.GetEnvironmentVariable("HK_MODLINKS_MIRROR_SKIP_URLS")?.Split('|').ToList()
	?? new();

HttpClient client = new();
List<Task> tasks = new();

try {
	Directory.Delete("dist", true);
} catch { }

try {
	Directory.Delete("temp", true);
} catch { }

try {
	File.Delete("dist.zip");
} catch { }

Directory.CreateDirectory("dist/apis");
Directory.CreateDirectory("dist/mods");
Directory.CreateDirectory("temp");

#region Download and parse ApiLinks.xml

XmlDocument apiLinksXml = new() {
	PreserveWhitespace = true
};
apiLinksXml.Load(
	client
		.GetStreamAsync("https://raw.githubusercontent.com/hk-modding/modlinks/main/ApiLinks.xml")
		.Result
);

#endregion

#region Download and parse ModLinks.xml

XmlDocument modLinksXml = new() {
	PreserveWhitespace = true
};
modLinksXml.Load(
	client
		.GetStreamAsync("https://raw.githubusercontent.com/hk-modding/modlinks/main/ModLinks.xml")
		.Result
);

#endregion

#region Download Apis

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

			Stream fileStream = File.Create($"dist/apis/{node.ParentNode.Name}.zip");
			task.Result.EnsureSuccessStatusCode().Content.ReadAsStream().CopyTo(fileStream);

			Console.WriteLine($"Downloaded {node.ParentNode.Name} api");
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

	XmlNode linkNode = null!;
	foreach (XmlNode node in modInfo["Link"]!.ChildNodes) {
		if (node.Name == "#cdata-section") {
			linkNode = node;
			break;
		}
	}

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

	linkNode.InnerText = urlBase + $"mods/{HttpUtility.HtmlEncode(fileName)}";

	Task downloadModtask = client
		.GetAsync(link)
		.ContinueWith(task => {
			HttpContent content = task.Result.EnsureSuccessStatusCode().Content;
			HttpContentHeaders contentHeaders = content.Headers;

			int approxSize = checked((int) (contentHeaders.ContentDisposition switch {
				ContentDispositionHeaderValue val when val.Size.HasValue => val.Size.Value,
				_ => contentHeaders.ContentLength.GetValueOrDefault(0)
			}));

			Stream resStream = content.ReadAsStream();
			using MemoryStream ms = new(approxSize);
			resStream.CopyTo(ms);
			resStream.Dispose();

			try {
				_ = new ZipArchive(ms, ZipArchiveMode.Read, true);

				ms.Position = 0;
				using Stream modFile = File.Create($"dist/mods/{fileName}");
				ms.CopyTo(modFile);
			} catch (InvalidDataException) {
				_ = Directory.CreateDirectory($"temp/{modName}");
				using (Stream tempFile = File.Create($"temp/{modName}/{modName}.dll")) {
					ms.CopyTo(tempFile);
				}

				ZipFile.CreateFromDirectory(
					$"temp/{modName}",
					$"dist/mods/{fileName}",
					CompressionLevel.SmallestSize,
					false
				);

				Console.WriteLine($"Compressed {name}");
			}

			if (name == modName) {
				Console.WriteLine($"Downloaded {fullModName}");
			} else {
				Console.WriteLine($"Downloaded {name} as {fullModName}");
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

#if !DEBUG
Directory.Delete("temp", true);
#endif

apiLinksXml.Save("dist/ApiLinks.xml");
modLinksXml.Save("dist/ModLinks.xml");

ZipFile.CreateFromDirectory("dist", "dist.zip", CompressionLevel.NoCompression, false);

using (SHA1 sha1 = SHA1.Create())
using (FileStream distZip = File.OpenRead("dist.zip")) {
	byte[] hash = sha1.ComputeHash(distZip);
	File.WriteAllText("dist/UPDATE_SHA.txt", Convert.ToHexString(hash) + '\n');
}

#if !DEBUG
File.Delete("dist.zip");
#endif
