// Self-test for the parts that need no API key. Built and run by tests\run.cmd; prints PASS/FAIL per check.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Prosper
{
    static class Tests
    {
        static int failed;
        static void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (ok || detail.Length == 0 ? "" : "  -> " + detail));
            if (!ok) failed++;
        }

        [STAThread]
        static int Main()
        {
            // Markdown answers render, and nothing in them can inject markup.
            string html = Markdown.Html("## Plan\n- **one** and `code`\n- two\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n<script>alert(1)</script>\nsee https://example.com.");
            Check("markdown heading", html.Contains("<h3>Plan</h3>"));
            Check("markdown list, bold, code", html.Contains("<ul><li><b>one</b> and <code>code</code></li><li>two</li></ul>"), html);
            Check("markdown table", html.Contains("<th>a</th>") && html.Contains("<td>2</td>"));
            Check("markdown escapes html", !html.Contains("<script>") && html.Contains("&lt;script&gt;"));
            Check("markdown links (no trailing dot)", html.Contains("<a href=\"https://example.com\">https://example.com</a>."), html);
            Check("plain preview", Markdown.Plain("**3** secrets in `prod`") == "3 secrets in prod");
            Check("snake_case kept", Markdown.Html("snake_case_name").Contains("snake_case_name"));

            // JSON round trip, the shapes OpenAI sends.
            var o = Json.Obj("{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"hi\"}]}],\"n\":3,\"ok\":true}");
            Check("json nested", Json.S_(Json.A(Json.A(o, "output")[0], "content")[0], "text") == "hi");
            Check("json number/bool", Json.S_(o, "n") == "3" && Json.S_(o, "ok") == "true");
            Check("json builder skips nulls", Json.Write(Json.O("a", 1, "b", null)) == "{\"a\":1}");

            // Secrets: sealed for this Windows user, readable back, unreadable when altered.
            string sealedKey = Secret.SealText("sk-test-123");
            Check("secret round trip", Secret.OpenText(sealedKey) == "sk-test-123" && !sealedKey.Contains("sk-test"));
            Check("secret tamper", Secret.OpenText("AAAA" + sealedKey.Substring(4)) == "");

            // What the Assistant receives.
            string m = Ai.AssistMessage("reply to this", new AssistContext { App = "Outlook", Selected = "see you at 5?", Screen = true, Audio = "File: call.m4a" });
            Check("assistant: selection, audio, screen before request",
                m.IndexOf("<selected_text>") >= 0 && m.IndexOf("<attached_audio_transcript>") > 0 && m.IndexOf("<screen>") > 0 && m.IndexOf("<screen>") < m.IndexOf("<request>"));
            Check("assistant: no screen note without a screenshot", !Ai.AssistMessage("hi", new AssistContext()).Contains("<screen>"));
            Check("tool description", Ai.Describe("api_request", "{\"method\":\"post\",\"url\":\"https://api.github.com/repos/x/y/issues\"}") == "POST api.github.com/repos/x/y/issues");
            Check("audio types", Ai.AudioType(".WAV") == "audio/wav" && Ai.AudioType("m4a") == "audio/mp4");

            // Never Ctrl+C into a terminal.
            Check("unknown window counts as a terminal (no Ctrl+C)", Target.IsTerminal(IntPtr.Zero));

            // Without a key, a polite message, and nothing is sent.
            var keep = Settings.Current.ApiKeySealed;
            Settings.Current.ApiKeySealed = "";
            try { Ai.Transcribe("none.wav"); Check("no key refused", false); }
            catch (Exception e) { Check("no key refused", e.Message.Contains("API key"), e.Message); }
            Settings.Current.ApiKeySealed = keep;

            // The microphone: one second through Windows' own audio (MCI).
            var rec = new Recorder("prosperselftest");
            string why = rec.Start();
            if (why != null) Check("microphone opens", false, why);
            else
            {
                Thread.Sleep(1000);
                int peak; string f = rec.Stop(out peak);
                long size = f != null ? new FileInfo(f).Length : 0;
                Check("microphone records a 16 kHz WAV", f != null && size > 20000, "size " + size);
                Console.WriteLine("      (loudest sample " + peak + "; 0 means Windows gave silence: check microphone privacy)");
                if (f != null) File.Delete(f);
            }

            Console.WriteLine(failed == 0 ? "All checks passed." : failed + " check(s) failed.");
            return failed == 0 ? 0 : 1;
        }
    }
}
