using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using HtmlAgilityPack;

namespace HTML_Editor
{
    public partial class HTMLEditor : Form
    {
        private string currentFilePath = "";
        private string currentEmailHtml = "";
        private string currentEmailDirectory = "";
        private string originalRawHtml = "";

        // Store original subject paragraph HTML to avoid HAP corruption
        private string originalSubjectParagraphHtml = "";
        private string originalSubjectText = "";

        public HTMLEditor()
        {
            InitializeComponent();
            InitializeWebView();
        }

        private async void InitializeWebView()
        {
            try
            {
                // This prepares the browser engine
                await webView.EnsureCoreWebView2Async(null);

                // This is the "Messproof" secret: 
                // We intercept requests to "http://email.content" and serve your local files.
                webView.CoreWebView2.WebResourceRequested += CoreWebView2_WebResourceRequested;
                webView.CoreWebView2.AddWebResourceRequestedFilter("http://email.content/*", CoreWebView2WebResourceContext.All);

                // Inject paste/drag handlers for local images after each navigation.
                webView.CoreWebView2.NavigationCompleted += async (s, e) =>
                {
                    if (e.IsSuccess)
                    {
                        await InjectImagePasteHandlersAsync();
                    }
                };
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error initializing browser: " + ex.Message);
            }
        }

        private async Task InjectImagePasteHandlersAsync()
        {
            string script = @"
(function () {
  if (window.__htmlEditorImagePasteHook) return;
  window.__htmlEditorImagePasteHook = true;

  function insertImage(dataUrl) {
    const img = document.createElement('img');
    img.src = dataUrl;
    const sel = window.getSelection();
    if (sel && sel.rangeCount > 0) {
      const range = sel.getRangeAt(0);
      range.deleteContents();
      range.insertNode(img);
      range.setStartAfter(img);
      range.setEndAfter(img);
      sel.removeAllRanges();
      sel.addRange(range);
    } else {
      (document.body || document.documentElement).appendChild(img);
    }
  }

  function handleFiles(files) {
    Array.from(files || []).forEach(file => {
      if (!file || !file.type || !file.type.startsWith('image/')) return;
      const reader = new FileReader();
      reader.onload = e => insertImage(e.target.result);
      reader.readAsDataURL(file);
    });
  }

  document.addEventListener('paste', function (e) {
    const dt = e.clipboardData;
    if (!dt) return;
    if (dt.files && dt.files.length) {
      e.preventDefault();
      handleFiles(dt.files);
      return;
    }
    if (dt.items) {
      const items = Array.from(dt.items);
      const imageItems = items.filter(i => i.kind === 'file' && i.type && i.type.startsWith('image/'));
      if (imageItems.length) {
        e.preventDefault();
        imageItems.forEach(i => handleFiles([i.getAsFile()]));
      }
    }
  });

  document.addEventListener('drop', function (e) {
    if (!e.dataTransfer || !e.dataTransfer.files || !e.dataTransfer.files.length) return;
    const hasImage = Array.from(e.dataTransfer.files).some(f => f.type && f.type.startsWith('image/'));
    if (!hasImage) return;
    e.preventDefault();
    handleFiles(e.dataTransfer.files);
  });

  document.addEventListener('dragover', function (e) {
    if (e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files.length) {
      e.preventDefault();
    }
  });
})();";

            await webView.ExecuteScriptAsync(script);
        }

        // This function handles loading images from your _files folder automatically - works
        private void CoreWebView2_WebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            string uri = e.Request.Uri;

            if (uri.Contains("editor.html"))
            {
                var stream = new MemoryStream(Encoding.UTF8.GetBytes(currentEmailHtml));
                e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(stream, 200, "OK", "Content-Type: text/html");
            }
            else if (uri.StartsWith("http://email.content/"))
            {
                string relativePath = uri.Replace("http://email.content/", "");
                if (relativePath.Contains("?")) relativePath = relativePath.Split('?')[0];

                string decodedPath = Uri.UnescapeDataString(relativePath).Replace('/', Path.DirectorySeparatorChar);
                string fullPath = Path.Combine(currentEmailDirectory, decodedPath);

                if (File.Exists(fullPath))
                {
                    string ext = Path.GetExtension(fullPath).ToLower();
                    string mime = "image/png";
                    if (ext == ".jpg" || ext == ".jpeg") mime = "image/jpeg";
                    else if (ext == ".gif") mime = "image/gif";
                    else if (ext == ".bmp") mime = "image/bmp";
                    else if (ext == ".webp") mime = "image/webp";

                    var stream = File.OpenRead(fullPath);
                    e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {mime}");
                }
            }
        }

        private void btnOpen_Click(object sender, EventArgs e) //works
        {
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "HTML files (*.htm;*.html)|*.htm;*.html|All files (*.*)|*.*";
                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    LoadEmail(openFileDialog.FileName);
                }
            }
        }

        private void LoadEmail(string filePath)
        {
            try
            {
                currentFilePath = filePath;
                currentEmailDirectory = Path.GetDirectoryName(filePath);

                // Load the HTML using UTF-8 so the WebView matches saved output.
                string rawHtml = File.ReadAllText(filePath, Encoding.UTF8);
                originalRawHtml = rawHtml;

                var doc = new HtmlAgilityPack.HtmlDocument();
                doc.LoadHtml(rawHtml);

                // 1. Extract Subject into the TextBox
                txtSubject.Text = ExtractSubject(doc);

                // 2. Make the body editable
                var bodyNode = doc.DocumentNode.SelectSingleNode("//body");
                if (bodyNode != null)
                {
                    bodyNode.Attributes.Add("contenteditable", "true");
                    bodyNode.Attributes.Add("style", "outline: none;");

                    // Add markers so we can safely extract the edited body later
                    if (bodyNode.InnerHtml.IndexOf("EDITOR_BODY_START", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        bodyNode.PrependChild(doc.CreateComment("EDITOR_BODY_START"));
                        bodyNode.AppendChild(doc.CreateComment("EDITOR_BODY_END"));
                    }
                }

                currentEmailHtml = NormalizeCharsetToUtf8(doc.DocumentNode.OuterHtml);

                // 3. Navigate to the virtual page
                webView.CoreWebView2.Navigate($"http://email.content/editor.html?t={DateTime.Now.Ticks}");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading email: " + ex.Message);
            }
        }

        private string ExtractSubject(HtmlAgilityPack.HtmlDocument doc)
        {
            // Read raw HTML directly - HAP corrupts malformed Outlook HTML
            string rawHtml = File.ReadAllText(currentFilePath, Encoding.UTF8);

            originalSubjectParagraphHtml = "";
            originalSubjectText = "";

            // Find the Subject paragraph in raw HTML (single line, minimal assumptions)
            var match = Regex.Match(
                rawHtml,
                @"<p\b[^>]*>.*?Subject:.*?</p>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            if (match.Success)
            {
                originalSubjectParagraphHtml = match.Value;

                originalSubjectText = ExtractSubjectTextFromParagraph(originalSubjectParagraphHtml);
            }

            // Hide the subject paragraph in HAP doc
            var paragraphs = doc.DocumentNode.SelectNodes("//p");
            if (paragraphs != null)
            {
                foreach (var p in paragraphs)
                {
                    string outerHtml = p.OuterHtml;
                    if (outerHtml.Contains("Subject") ||
                        outerHtml.Contains("MsoNormal") && outerHtml.Contains("margin-left:135"))
                    {
                        p.SetAttributeValue("style", "display:none;");
                        p.SetAttributeValue("data-subject-para", "true");
                        break;
                    }
                }
            }

            return string.IsNullOrEmpty(originalSubjectText) ? "Subject not found" : originalSubjectText;
        }

        private async void btnSave_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(currentFilePath)) return;
            try
            {
                await SaveEmailAsync(currentFilePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error during save: " + ex.Message);
            }
        }

        private async void btnSaveAs_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(currentFilePath)) return;

            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.Filter = "HTML files (*.htm;*.html)|*.htm;*.html";
                sfd.FileName = Path.GetFileName(currentFilePath);

                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        // SaveEmailAsync now handles copying images from the old location automatically
                        await SaveEmailAsync(sfd.FileName);

                        // Now update our tracking to the new file
                        currentFilePath = sfd.FileName;
                        currentEmailDirectory = Path.GetDirectoryName(sfd.FileName);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Error during save as: " + ex.Message);
                    }
                }
            }
        }

        private async void btnBold_Click(object sender, EventArgs e)
        {
            await webView.ExecuteScriptAsync("document.execCommand('bold', false, null);");
        }

        private async void btnItalic_Click(object sender, EventArgs e)
        {
            await webView.ExecuteScriptAsync("document.execCommand('italic', false, null);");
        }

        private async void btnUnderline_Click(object sender, EventArgs e)
        {
            await webView.ExecuteScriptAsync("document.execCommand('underline', false, null);");
        }

        private async void cmbFontFamily_SelectedIndexChanged(object sender, EventArgs e)
        {
            string font = cmbFontFamily.SelectedItem.ToString();
            await webView.ExecuteScriptAsync($"document.execCommand('fontName', false, '{font}');");
        }

        private async void cmbFontSize_SelectedIndexChanged(object sender, EventArgs e)
        {
            string size = cmbFontSize.SelectedItem.ToString();
            // execCommand font size uses 1-7
            string webSize = "3";
            switch (size)
            {
                case "8": webSize = "1"; break;
                case "10": webSize = "2"; break;
                case "12": webSize = "3"; break;
                case "14": webSize = "4"; break;
                case "18": webSize = "5"; break;
                case "24": webSize = "6"; break;
                case "36": webSize = "7"; break;
            }
            await webView.ExecuteScriptAsync($"document.execCommand('fontSize', false, '{webSize}');");
        }

        private async void btnAlignLeft_Click(object sender, EventArgs e)
        {
            await webView.ExecuteScriptAsync("document.execCommand('justifyLeft', false, null);");
        }

        private async void btnAlignCenter_Click(object sender, EventArgs e)
        {
            await webView.ExecuteScriptAsync("document.execCommand('justifyCenter', false, null);");
        }

        private async void btnAlignRight_Click(object sender, EventArgs e)
        {
            await webView.ExecuteScriptAsync("document.execCommand('justifyRight', false, null);");
        }

        private async void btnUndo_Click(object sender, EventArgs e)
        {
            await webView.ExecuteScriptAsync("document.execCommand('undo', false, null);");
        }

        private async void btnRedo_Click(object sender, EventArgs e)
        {
            await webView.ExecuteScriptAsync("document.execCommand('redo', false, null);");
        }

        private async void btnColor_Click(object sender, EventArgs e)
        {
            using (ColorDialog cd = new ColorDialog())
            {
                if (cd.ShowDialog() == DialogResult.OK)
                {
                    string hexColor = $"#{cd.Color.R:X2}{cd.Color.G:X2}{cd.Color.B:X2}";
                    await webView.ExecuteScriptAsync($"document.execCommand('foreColor', false, '{hexColor}');");
                }
            }
        }

        private async Task SaveEmailAsync(string targetPath)
        {
            try
            {
                // 1. Get edited HTML from browser
                // Convert any blob: images (e.g. pasted images) into data: URLs before fetching
                string scriptResult = await webView.ExecuteScriptAsync(@"(async () => {
  try {
    const imgs = Array.from(document.querySelectorAll('img')).filter(i => (i.currentSrc || i.src || '').startsWith('blob:'));
    for (const img of imgs) {
      const url = img.currentSrc || img.src;
      const resp = await fetch(url);
      const blob = await resp.blob();
      const dataUrl = await new Promise(resolve => {
        const fr = new FileReader();
        fr.onload = () => resolve(fr.result);
        fr.readAsDataURL(blob);
      });
      img.src = dataUrl;
    }
  } catch (e) { /* ignore */ }
  return document.documentElement.outerHTML;
})();");
                string editedHtml = UnwrapWebViewScriptStringResult(scriptResult);

                if (!IsProbablyHtml(editedHtml))
                {
                    string simpleResult = await webView.ExecuteScriptAsync("document.documentElement.outerHTML");
                    editedHtml = UnwrapWebViewScriptStringResult(simpleResult);
                }

                if (!IsProbablyHtml(editedHtml))
                {
                    MessageBox.Show("Nothing to save. The editor did not return HTML.");
                    return;
                }

                // 2. Setup paths
                string targetDir = Path.GetFullPath(Path.GetDirectoryName(targetPath));
                string filesFolderName = Path.GetFileNameWithoutExtension(targetPath) + "_files";
                string filesFolderPath = Path.Combine(targetDir, filesFolderName);

                // 3. Extract and process the body content
                string editedBodyInner = ExtractEditedBodyInner(editedHtml);
                var bodyDoc = new HtmlAgilityPack.HtmlDocument();
                bodyDoc.LoadHtml(editedBodyInner ?? string.Empty);

                // Remove editing markers
                var editableNodes = bodyDoc.DocumentNode.SelectNodes("//*[@contenteditable]");
                if (editableNodes != null)
                {
                    foreach (var n in editableNodes)
                    {
                        n.Attributes.Remove("contenteditable");
                        n.Attributes.Remove("style");
                    }
                }

                // 4. Process images: normalize/copy/persist images and update HTML paths.
                // Returns the filenames that are actually referenced in the HTML or XML.
                string sourceFilesFolderName = !string.IsNullOrEmpty(currentFilePath) ? Path.GetFileNameWithoutExtension(currentFilePath) + "_files" : "";
                string sourceFilesFolderPath = !string.IsNullOrEmpty(currentEmailDirectory) ? Path.Combine(currentEmailDirectory, sourceFilesFolderName) : "";
                HashSet<string> usedFiles = ProcessImagesForSave(
                    bodyDoc,
                    filesFolderPath,
                    filesFolderName,
                    sourceFilesFolderPath,
                    sourceFilesFolderName);

                // Copy non-image files (e.g., XML/theme data) into the new _files folder on Save As.
                if (!string.IsNullOrEmpty(sourceFilesFolderPath))
                {
                    CopyNonImageFiles(sourceFilesFolderPath, filesFolderPath);
                }

                // Update filelist.xml to list only images referenced by the email.
                UpdateFileListXml(filesFolderPath, targetPath, usedFiles);

                // NOTE: We no longer delete any files from _files so users can undo safely.

                // 5. Reconstruct Subject paragraph
                string bodyFragment = ReconstructSubject(bodyDoc.DocumentNode.InnerHtml, txtSubject.Text);

                // 6. Final Assembly: Merge with original head or create new doc
                string finalHtml;
                if (!string.IsNullOrWhiteSpace(originalRawHtml) && TryReplaceBodyInnerHtml(originalRawHtml, bodyFragment, out var mergedHtml))
                {
                    finalHtml = mergedHtml;
                }
                else
                {
                    // Fallback: If merging fails, we must still use the processed bodyFragment
                    finalHtml = $"<!DOCTYPE html><html><head><meta charset=\"windows-1252\"></head><body>{bodyFragment}</body></html>";
                }

                // Ensure the output HTML declares UTF-8 so Unicode characters are preserved.
                finalHtml = NormalizeCharsetToUtf8(finalHtml);
                File.WriteAllText(targetPath, finalHtml, new UTF8Encoding(false));
                MessageBox.Show($"Saved! Found {usedFiles.Count} images.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving: " + ex.Message);
            }
        }

        // Image pipeline: collect all image references, persist pasted images, normalize paths,
        // and return the set of filenames that are still referenced.
        private HashSet<string> ProcessImagesForSave(
            HtmlAgilityPack.HtmlDocument doc,
            string filesFolderPath,
            string filesFolderName,
            string sourceFilesFolderPath,
            string sourceFilesFolderName)
        {
            var usedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Find all image tags, including Outlook's VML <v:imagedata> nodes.
            var nodes = doc.DocumentNode.Descendants()
                .Where(n => n.Name.Equals("img", StringComparison.OrdinalIgnoreCase) ||
                           n.Name.Equals("imagedata", StringComparison.OrdinalIgnoreCase) ||
                           n.Name.EndsWith(":imagedata", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (nodes.Count == 0) return usedFiles;

            // Ensure the directory exists if there are any images to save.
            if (!Directory.Exists(filesFolderPath))
            {
                try { Directory.CreateDirectory(filesFolderPath); }
                catch (Exception ex) { MessageBox.Show("Could not create images folder: " + ex.Message); return usedFiles; }
            }

            int newImageCounter = GetNextNewImageCounter(filesFolderPath);

            foreach (var node in nodes)
            {
                // Detect the correct attribute (Outlook imagedata can use src, v:src, or o:href).
                string srcAttr = "src";
                if (node.Name.Contains("imagedata"))
                {
                    if (node.Attributes.Contains("src")) srcAttr = "src";
                    else if (node.Attributes.Contains("v:src")) srcAttr = "v:src";
                    else if (node.Attributes.Contains("o:href")) srcAttr = "o:href";
                }

                string src = node.GetAttributeValue(srcAttr, "").Trim();
                if (string.IsNullOrWhiteSpace(src)) continue;

                string newFileName = null;

                // 1. Pasted images arrive as data: URLs; persist them to disk and replace src.
                if (src.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                {
                    bool dataUrlFailed = false;
                    try
                    {
                        int commaIndex = src.IndexOf(',');
                        if (commaIndex > 0)
                        {
                            string header = src.Substring(0, commaIndex);
                            string base64Data = src.Substring(commaIndex + 1).Trim();

                            // Determine file extension from the data URL header.
                            string ext = ".png";
                            if (header.Contains("image/jpeg")) ext = ".jpg";
                            else if (header.Contains("image/gif")) ext = ".gif";
                            else if (header.Contains("image/bmp")) ext = ".bmp";
                            else if (header.Contains("image/webp")) ext = ".webp";

                            // Generate a unique filename in the _files folder.
                            newFileName = $"new_image{newImageCounter:000}{ext}";
                            while (File.Exists(Path.Combine(filesFolderPath, newFileName)))
                            {
                                newImageCounter++;
                                newFileName = $"new_image{newImageCounter:000}{ext}";
                            }

                            byte[] bytes = Convert.FromBase64String(base64Data);
                            File.WriteAllBytes(Path.Combine(filesFolderPath, newFileName), bytes);
                            newImageCounter++;
                        }
                    }
                    catch (Exception ex)
                    {
                        dataUrlFailed = true;
                        System.Diagnostics.Debug.WriteLine("Failed to save pasted image: " + ex.Message);
                        MessageBox.Show("Failed to save a pasted image. It will remain embedded in the HTML until saved correctly.\n\n" + ex.Message);
                    }

                    // If conversion failed, keep the original data URL so the image does not disappear.
                    if (dataUrlFailed) newFileName = null;
                }
                // 2. Existing images: resolve to a local file and ensure it exists in _files.
                else
                {
                    string srcNoQuery = src.Split('?')[0];
                    string unescapedSrc = Uri.UnescapeDataString(srcNoQuery);
                    string sourcePath = null;

                    // If it's a browser virtual path, translate it to the original directory.
                    if (unescapedSrc.StartsWith("http://email.content/", StringComparison.OrdinalIgnoreCase))
                    {
                        string rel = unescapedSrc.Replace("http://email.content/", "");
                        sourcePath = Path.Combine(currentEmailDirectory, rel.Replace('/', Path.DirectorySeparatorChar));
                    }
                    // If it's already a relative path (e.g. from a previous save), resolve it.
                    else if (!unescapedSrc.Contains("://") && !Path.IsPathRooted(unescapedSrc))
                    {
                        sourcePath = Path.Combine(currentEmailDirectory, unescapedSrc.Replace('/', Path.DirectorySeparatorChar));
                    }

                    // Fallback: if the HTML points to a wrong _files folder name, use just the filename
                    // and look for it in the current email's _files folder.
                    if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                    {
                        string fileName = Path.GetFileName(unescapedSrc.Replace('/', Path.DirectorySeparatorChar));
                        if (!string.IsNullOrEmpty(fileName))
                        {
                            string candidate = Path.Combine(currentEmailDirectory, sourceFilesFolderName, fileName);
                            if (File.Exists(candidate))
                            {
                                sourcePath = candidate;
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(sourcePath) && File.Exists(sourcePath))
                    {
                        newFileName = Path.GetFileName(sourcePath);
                        string destPath = Path.Combine(filesFolderPath, newFileName);

                        // Copy to the target _files folder if it's not already there (important for Save As).
                        if (string.Compare(Path.GetFullPath(sourcePath), Path.GetFullPath(destPath), true) != 0)
                        {
                            try { File.Copy(sourcePath, destPath, true); }
                            catch { /* ignore copy errors for locked files */ }
                        }
                    }
                }

                // If we successfully saved/located the file, update the HTML and mark it as used.
                if (!string.IsNullOrEmpty(newFileName))
                {
                    node.SetAttributeValue(srcAttr, $"{filesFolderName}/{newFileName}");
                    usedFiles.Add(newFileName);
                }
            }

            // Also keep any image files referenced in Outlook XML (e.g., filelist.xml).
            MergeXmlReferencedImages(sourceFilesFolderPath, filesFolderPath, usedFiles);

            return usedFiles;
        }

        private static int GetNextNewImageCounter(string filesFolderPath)
        {
            if (string.IsNullOrWhiteSpace(filesFolderPath) || !Directory.Exists(filesFolderPath))
            {
                return 1;
            }

            int max = 0;
            foreach (var file in Directory.GetFiles(filesFolderPath))
            {
                string name = Path.GetFileName(file);
                var match = Regex.Match(name, @"^new_image(\d{3})\.", RegexOptions.IgnoreCase);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int n))
                {
                    if (n > max) max = n;
                }
            }

            return max + 1;
        }

        private static void MergeXmlReferencedImages(string sourceFilesFolderPath, string targetFilesFolderPath, HashSet<string> usedFiles)
        {
            if (string.IsNullOrWhiteSpace(sourceFilesFolderPath) || !Directory.Exists(sourceFilesFolderPath)) return;

            string fileListPath = Path.Combine(sourceFilesFolderPath, "filelist.xml");
            if (!File.Exists(fileListPath)) return;

            string xml;
            try
            {
                xml = File.ReadAllText(fileListPath);
            }
            catch
            {
                return;
            }

            var matches = Regex.Matches(
                xml,
                @"([A-Za-z0-9 _\-\.\(\)]+)\.(png|jpg|jpeg|gif|bmp|webp|tif|tiff|ico|emf|wmf)",
                RegexOptions.IgnoreCase);

            foreach (Match match in matches)
            {
                if (!match.Success) continue;
                string fileName = match.Value;
                if (!IsImageFileName(fileName)) continue;

                usedFiles.Add(fileName);

                string sourcePath = Path.Combine(sourceFilesFolderPath, fileName);
                if (!File.Exists(sourcePath)) continue;

                if (!Directory.Exists(targetFilesFolderPath))
                {
                    try { Directory.CreateDirectory(targetFilesFolderPath); }
                    catch { return; }
                }

                string destPath = Path.Combine(targetFilesFolderPath, fileName);
                if (!File.Exists(destPath))
                {
                    try { File.Copy(sourcePath, destPath, true); } catch { }
                }
            }
        }

        private static void CopyNonImageFiles(string sourceFilesFolderPath, string targetFilesFolderPath)
        {
            if (string.IsNullOrWhiteSpace(sourceFilesFolderPath) || !Directory.Exists(sourceFilesFolderPath)) return;

            if (!Directory.Exists(targetFilesFolderPath))
            {
                try { Directory.CreateDirectory(targetFilesFolderPath); }
                catch { return; }
            }

            foreach (var file in Directory.GetFiles(sourceFilesFolderPath))
            {
                string fileName = Path.GetFileName(file);
                if (IsImageFileName(fileName)) continue;

                string destPath = Path.Combine(targetFilesFolderPath, fileName);
                try { File.Copy(file, destPath, true); } catch { /* ignore copy errors */ }
            }
        }

        private static void UpdateFileListXml(string targetFilesFolderPath, string targetHtmlPath, HashSet<string> usedFiles)
        {
            if (string.IsNullOrWhiteSpace(targetFilesFolderPath) || !Directory.Exists(targetFilesFolderPath)) return;
            if (usedFiles == null) return;

            string htmlFileName = Path.GetFileName(targetHtmlPath);
            if (string.IsNullOrWhiteSpace(htmlFileName)) return;

            string mainHref = "../" + Uri.EscapeDataString(htmlFileName);
            var imageFiles = usedFiles
                .Where(name => IsImageFileName(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("<xml xmlns:o=\"urn:schemas-microsoft-com:office:office\">");
            sb.AppendLine($" <o:MainFile HRef=\"{mainHref}\"/>");

            foreach (var file in imageFiles)
            {
                string href = Uri.EscapeDataString(file);
                sb.AppendLine($" <o:File HRef=\"{href}\"/>");
            }

            sb.AppendLine("</xml>");

            string fileListPath = Path.Combine(targetFilesFolderPath, "filelist.xml");
            try
            {
                File.WriteAllText(fileListPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch
            {
                // Ignore filelist write errors to avoid blocking save.
            }
        }

        private static bool IsImageFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return false;
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif"
                || ext == ".bmp" || ext == ".webp" || ext == ".tif" || ext == ".tiff"
                || ext == ".ico" || ext == ".emf" || ext == ".wmf";
        }

        private void ManageFilesFolder(string oldPath, string newPath)
        {
            if (string.IsNullOrEmpty(oldPath)) return;

            string oldFolder = Path.Combine(Path.GetDirectoryName(oldPath), Path.GetFileNameWithoutExtension(oldPath) + "_files");
            string newFolder = Path.Combine(Path.GetDirectoryName(newPath), Path.GetFileNameWithoutExtension(newPath) + "_files");

            if (Directory.Exists(oldFolder) &&
                string.Compare(Path.GetFullPath(oldFolder), Path.GetFullPath(newFolder), StringComparison.OrdinalIgnoreCase) != 0)
            {
                if (!Directory.Exists(newFolder)) Directory.CreateDirectory(newFolder);
                foreach (string file in Directory.GetFiles(oldFolder))
                {
                    try
                    {
                        File.Copy(file, Path.Combine(newFolder, Path.GetFileName(file)), true);
                    }
                    catch { /* ignore copy errors for individual files */ }
                }
            }
        }

        private static bool TryReplaceBodyInnerHtml(string originalHtml, string newBodyInner, out string mergedHtml)
        {
            mergedHtml = null;
            if (string.IsNullOrWhiteSpace(originalHtml)) return false;
            if (newBodyInner == null) return false;

            int bodyOpen = originalHtml.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
            if (bodyOpen < 0) return false;
            int bodyStart = originalHtml.IndexOf(">", bodyOpen);
            if (bodyStart < 0) return false;

            int bodyClose = originalHtml.IndexOf("</body>", bodyStart, StringComparison.OrdinalIgnoreCase);
            if (bodyClose < 0) return false;

            mergedHtml = originalHtml.Substring(0, bodyStart + 1)
                + newBodyInner
                + originalHtml.Substring(bodyClose);
            return true;
        }

        private string ReconstructSubject(string html, string newSubject)
        {
            // Reconstruct the Subject paragraph in the specific format requested
            string newSubjectParaHtml = $@"<p class=MsoNormal style='margin-left:135.0pt;text-indent:-135.0pt;tab-stops:135pt;mso-layout-grid-align:none;text-autospace:none'><b><span lang=EN-US style='font-family:""Calibri"",sans-serif;color:black;'>Subject:<span style='mso-tab-count:1'></span></span></b><span lang=EN-US style='font-family:""Calibri"",sans-serif;mso-font-kerning:0pt'>{WebUtility.HtmlEncode(newSubject)}<o:p></o:p></span></p>";

            // Try replacing the paragraph that has our marker
            int markerPos = html.IndexOf("data-subject-para=\"true\"", StringComparison.OrdinalIgnoreCase);
            if (markerPos >= 0)
            {
                int pStart = html.LastIndexOf("<p", markerPos, StringComparison.OrdinalIgnoreCase);
                int pEndTag = html.IndexOf("</p>", markerPos, StringComparison.OrdinalIgnoreCase);
                if (pStart >= 0 && pEndTag >= 0)
                {
                    string oldPara = html.Substring(pStart, pEndTag - pStart + 4);
                    return html.Replace(oldPara, newSubjectParaHtml);
                }
            }

            // Fallback: look for Subject: text in a paragraph
            var match = Regex.Match(
                html,
                @"<p\b[^>]*>.*?Subject:.*?</p>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            if (match.Success)
            {
                return html.Remove(match.Index, match.Length).Insert(match.Index, newSubjectParaHtml);
            }

            // If not found, prepend it
            return newSubjectParaHtml + html;
        }

        private static string ExtractEditedBodyInner(string editedHtml)
        {
            if (string.IsNullOrWhiteSpace(editedHtml)) return "";

            const string startMarker = "<!--EDITOR_BODY_START-->";
            const string endMarker = "<!--EDITOR_BODY_END-->";

            int start = editedHtml.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
            int end = editedHtml.IndexOf(endMarker, StringComparison.OrdinalIgnoreCase);
            if (start >= 0 && end > start)
            {
                start += startMarker.Length;
                return editedHtml.Substring(start, end - start);
            }

            // Fallback to body inner html
            int bodyOpen = editedHtml.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
            if (bodyOpen < 0) return editedHtml;
            int bodyStart = editedHtml.IndexOf(">", bodyOpen);
            if (bodyStart < 0) return editedHtml;
            int bodyClose = editedHtml.IndexOf("</body>", bodyStart, StringComparison.OrdinalIgnoreCase);
            if (bodyClose < 0) return editedHtml;

            return editedHtml.Substring(bodyStart + 1, bodyClose - bodyStart - 1);
        }

        private static void EnsureHtmlAndBody(HtmlAgilityPack.HtmlDocument doc)
        {
            var htmlNode = doc.DocumentNode.SelectSingleNode("//html");
            var bodyNode = doc.DocumentNode.SelectSingleNode("//body");

            if (htmlNode == null)
            {
                htmlNode = doc.CreateElement("html");
                doc.DocumentNode.AppendChild(htmlNode);
            }

            if (bodyNode == null)
            {
                bodyNode = doc.CreateElement("body");
                htmlNode.AppendChild(bodyNode);

                // Move non-html/head nodes into body
                var toMove = doc.DocumentNode.ChildNodes
                    .Where(n => n.Name != "html" && n.Name != "#document")
                    .ToList();

                foreach (var n in toMove)
                {
                    n.Remove();
                    bodyNode.AppendChild(n);
                }
            }
        }

        private static string UnwrapWebViewScriptStringResult(string scriptResult)
        {
            // WebView2 ExecuteScriptAsync returns JSON-encoded values (often a quoted JSON string).
            if (scriptResult == null) return null;

            string trimmed = scriptResult.Trim();
            if (trimmed.Length == 0) return "";

            // Common non-JSON returns if the script fails or returns undefined in some cases
            if (string.Equals(trimmed, "undefined", StringComparison.OrdinalIgnoreCase)) return "";
            if (string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase)) return "";

            // Normal case: JSON string (e.g. "\"<html>...\"")
            // Avoid JsonSerializer here to prevent first-chance JsonException spam.
            if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"')
            {
                return JsonStringUnescape(trimmed.Substring(1, trimmed.Length - 2));
            }

            // Fallback: sometimes callers end up with a raw string already
            return trimmed;
        }

        private static string JsonStringUnescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;

            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (i + 1 >= s.Length)
                {
                    sb.Append('\\');
                    break;
                }

                char esc = s[++i];
                switch (esc)
                {
                    case '"': sb.Append('\"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 < s.Length)
                        {
                            string hex = s.Substring(i + 1, 4);
                            int code;
                            if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                            else
                            {
                                sb.Append("\\u");
                            }
                        }
                        else
                        {
                            sb.Append("\\u");
                        }
                        break;
                    default:
                        sb.Append(esc);
                        break;
                }
            }

            return sb.ToString();
        }

        private static bool IsProbablyHtml(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.TrimStart();
            return t.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0
                || t.IndexOf("<body", StringComparison.OrdinalIgnoreCase) >= 0
                || t.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeCharsetToUtf8(string html)
        {
            if (string.IsNullOrEmpty(html)) return html;

            // Replace <meta charset="..."> if present.
            string updated = Regex.Replace(
                html,
                @"(<meta\b[^>]*charset\s*=\s*)(['""]?)[^'"">\s]+(\2[^>]*>)",
                "$1$2utf-8$3",
                RegexOptions.IgnoreCase);

            // Replace content-type meta if present.
            updated = Regex.Replace(
                updated,
                @"(<meta\b[^>]*http-equiv\s*=\s*['""]?content-type['""]?[^>]*content\s*=\s*['""]text/html;\s*charset=)([^'""]+)(['""])",
                "$1utf-8$3",
                RegexOptions.IgnoreCase);

            // If no charset meta is found, insert one after <head>.
            if (!Regex.IsMatch(updated, @"<meta\b[^>]*charset\s*=", RegexOptions.IgnoreCase))
            {
                updated = Regex.Replace(
                    updated,
                    @"<head\b[^>]*>",
                    m => m.Value + "<meta charset=\"utf-8\">",
                    RegexOptions.IgnoreCase);
            }

            return updated;
        }

        private string ExtractSubjectTextFromParagraph(string paragraphHtml)
        {
            int boldEnd = paragraphHtml.IndexOf("</b>", StringComparison.OrdinalIgnoreCase);
            if (boldEnd >= 0)
            {
                int spanOpen = paragraphHtml.IndexOf("<span", boldEnd, StringComparison.OrdinalIgnoreCase);
                if (spanOpen >= 0)
                {
                    string after = paragraphHtml.Substring(spanOpen);
                    int spanTagEnd = after.IndexOf(">", StringComparison.OrdinalIgnoreCase);
                    int spanClose = after.IndexOf("</span>", spanTagEnd + 1, StringComparison.OrdinalIgnoreCase);
                    if (spanTagEnd >= 0 && spanClose > spanTagEnd)
                    {
                        string spanInner = after.Substring(spanTagEnd + 1, spanClose - spanTagEnd - 1);
                        int oP = spanInner.IndexOf("<o:p>", StringComparison.OrdinalIgnoreCase);
                        if (oP >= 0)
                            spanInner = spanInner.Substring(0, oP);

                        string cleaned = Regex.Replace(spanInner, "<[^>]+>", string.Empty);
                        return WebUtility.HtmlDecode(cleaned).Trim();
                    }
                }

                int end = paragraphHtml.IndexOf("<o:p>", boldEnd, StringComparison.OrdinalIgnoreCase);
                if (end < 0)
                    end = paragraphHtml.IndexOf("</p>", boldEnd, StringComparison.OrdinalIgnoreCase);
                if (end > boldEnd)
                {
                    string between = paragraphHtml.Substring(boldEnd + 4, end - (boldEnd + 4));
                    string cleaned = Regex.Replace(between, "<[^>]+>", string.Empty);
                    return WebUtility.HtmlDecode(cleaned).Trim();
                }
            }

            string plainText = Regex.Replace(paragraphHtml, "<[^>]+>", string.Empty);
            plainText = WebUtility.HtmlDecode(plainText);
            int idx = plainText.IndexOf("Subject:", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                return plainText.Substring(idx + "Subject:".Length).Trim();

            return plainText.Trim();
        }
    }
}