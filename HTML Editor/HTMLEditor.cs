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
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error initializing browser: " + ex.Message);
            }
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

                string fullPath = Path.Combine(currentEmailDirectory, Uri.UnescapeDataString(relativePath));

                if (File.Exists(fullPath))
                {
                    var stream = File.OpenRead(fullPath);
                    e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(stream, 200, "OK", "Content-Type: image/png"); // Simplification for images
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

                // Load the HTML using Windows-1252 encoding (standard for Outlook)
                string rawHtml = File.ReadAllText(filePath, Encoding.GetEncoding("windows-1252"));
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

                currentEmailHtml = doc.DocumentNode.OuterHtml;

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
            string rawHtml = File.ReadAllText(currentFilePath, Encoding.GetEncoding("windows-1252"));

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
            await SaveEmailAsync(currentFilePath);
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
                    await SaveEmailAsync(sfd.FileName);
                    currentFilePath = sfd.FileName;
                    currentEmailDirectory = Path.GetDirectoryName(sfd.FileName);
                }
            }
        }

        private async Task SaveEmailAsync(string targetPath)
        {
            try
            {
                // Get edited HTML from browser
                string scriptResult = await webView.ExecuteScriptAsync("document.documentElement.outerHTML");
                string editedHtml = UnwrapWebViewScriptStringResult(scriptResult);

                if (!IsProbablyHtml(editedHtml))
                {
                    MessageBox.Show("Nothing to save. The editor did not return HTML.");
                    return;
                }

                // 1. Extract just the body content between our markers (preserves Outlook head/styles)
                string editedBodyInner = ExtractEditedBodyInner(editedHtml);

                // 2. Clean up "editable" markers inside the body fragment
                var bodyDoc = new HtmlAgilityPack.HtmlDocument();
                bodyDoc.LoadHtml(editedBodyInner ?? string.Empty);

                var editableNodes = bodyDoc.DocumentNode.SelectNodes("//*[@contenteditable]");
                if (editableNodes != null)
                {
                    foreach (var n in editableNodes)
                    {
                        n.Attributes.Remove("contenteditable");
                        n.Attributes.Remove("style");
                    }
                }

                // 3. Reconstruct the Subject paragraph in the specific format requested
                var subjectPara = bodyDoc.DocumentNode.SelectSingleNode("//p[@data-subject-para='true']");
                string newSubjectParaHtml = $@"<p class=MsoNormal style='margin-left:135.0pt;text-indent:-135.0pt;tab-stops:135pt;mso-layout-grid-align:none;text-autospace:none'><b><span lang=EN-US style='font-family:""Calibri"",sans-serif;color:black;'>Subject:<span style='mso-tab-count:1'></span></span></b><span lang=EN-US style='font-family:""Calibri"",sans-serif;mso-font-kerning:0pt'>{WebUtility.HtmlEncode(txtSubject.Text)}<o:p></o:p></span></p>";

                if (subjectPara != null)
                {
                    var newNode = HtmlNode.CreateNode(newSubjectParaHtml);
                    subjectPara.ParentNode.ReplaceChild(newNode, subjectPara);
                }
                else
                {
                    // If not found, prepend it to the body
                    var newNode = HtmlNode.CreateNode(newSubjectParaHtml);
                    bodyDoc.DocumentNode.PrependChild(newNode);
                }

                string bodyFragment = bodyDoc.DocumentNode.InnerHtml;

                // 4. Preserve original head/attributes for Outlook: replace only the <body> content
                string finalHtml = editedHtml;
                if (!string.IsNullOrWhiteSpace(originalRawHtml)
                    && TryReplaceBodyInnerHtml(originalRawHtml, bodyFragment, out var mergedHtml))
                {
                    finalHtml = mergedHtml;
                }

                File.WriteAllText(targetPath, finalHtml, Encoding.GetEncoding("windows-1252"));
                MessageBox.Show("Saved!");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving: " + ex.Message);
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