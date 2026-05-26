// Services/OpenAIService.cs
using Autodesk.Revit.DB;
using ClashResolveAI.AutoResolve;
using ClashResolveAI.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace ClashResolveAI.OpenAI
{
    public class OpenAIService
    {
        private const string Url = "https://api.openai.com/v1/chat/completions";
        private readonly string _key;
        public string Model { get; set; }
        private static readonly HttpClient _http = new HttpClient() { Timeout = TimeSpan.FromSeconds(25) };

        public OpenAIService(string key, string? model = null)
        {
            _key  = key;
            Model = string.IsNullOrWhiteSpace(model) ? "gpt-4o" : model!;
        }

        public async Task<(string suggestion, string rfi)> AnalyseAsync(ClashResult c)
        {
            try
            {
                var body = new { model=Model, max_tokens=1024, temperature=0.2,
                    messages=new[]{ new{role="system",content=SysPrompt()}, new{role="user",content=Prompt(c)} }};
                var resp = await Post(body);
                return resp == null ? (Rule(c), AutoResolveEngine.ToRfiText(c)) : Parse(resp);
            }
            catch { return (Rule(c), AutoResolveEngine.ToRfiText(c)); }
        }

        public async Task<string> QuickAlertAsync(ClashResult c)
        {
            try
            {
                string linkCtx = string.IsNullOrEmpty(c.LinkFileA) ? "" : $" [Links: {c.LinkFileA}/{c.LinkFileB}]";
                var body = new { model=Model, max_tokens=100, temperature=0.1,
                    messages=new[]{ new{role="system",content="MEP coordinator. ONE sentence max 25 words: which element moves and how much. No JSON."},
                        new{role="user",content=$"CLASH:{c.DisciplineA}[{c.ElementA.Category?.Name}] vs {c.DisciplineB}[{c.ElementB.Category?.Name}]. Severity:{c.Severity} Gap:{c.GapMM:F0}mm Level:{c.LevelName} Grid:{c.GridRef}{linkCtx}. Priority:Structural>GravityDrain>HVAC>Elec>Plumbing>Fire"}}};
                var resp = await Post(body);
                return resp?["choices"]?[0]?["message"]?["content"]?.ToString() ?? Rule(c);
            }
            catch { return Rule(c); }
        }

        public async Task ProcessBatchAsync(List<ClashResult> clashes, System.IProgress<(int,int)>? prog=null)
        {
            for (int i=0;i<clashes.Count;i++)
            {
                var c=clashes[i];
                if (c.Severity==ClashSeverity.Clearance && c.Priority=="Low")
                { c.AiSuggestion=Rule(c); c.RfiText=AutoResolveEngine.ToRfiText(c); }
                else
                { var(s,r)=await AnalyseAsync(c); c.AiSuggestion=s; c.RfiText=r; }
                prog?.Report((i+1,clashes.Count));
                await Task.Delay(1100);
            }
        }

        private async Task<JObject?> Post(object payload)
        {
            var req=new HttpRequestMessage(HttpMethod.Post,Url);
            req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",_key);
            req.Content=new StringContent(JsonConvert.SerializeObject(payload),Encoding.UTF8,"application/json");
            var resp=await _http.SendAsync(req);
            if(!resp.IsSuccessStatusCode) return null;
            return JObject.Parse(await resp.Content.ReadAsStringAsync());
        }

        private static string SysPrompt()=>
            "You are a senior MEP BIM coordinator (20 years). Analyse clashes. " +
            "Priority(never violate): Structural(0)>GravityDrain(1)>HVAC(2)>Electrical(3)>Plumbing(4)>Fire(5). " +
            "Return ONLY valid JSON: {\"suggestion\":\"...\",\"rfi\":\"...\"}. No markdown.";

        private static string Prompt(ClashResult c)=>
            $"ID:{c.ClashId} Sev:{c.Severity} Pri:{c.Priority} Gap:{c.GapMM:F1}mm\n" +
            $"A:{c.ElementA.Category?.Name}(ID:{c.ElementA.Id.Value},{c.DisciplineA})\n" +
            $"B:{c.ElementB.Category?.Name}(ID:{c.ElementB.Id.Value},{c.DisciplineB})\n" +
            $"Level:{c.LevelName} Grid:{c.GridRef} Loc:{c.LocationText}\n" +
            $"Rule:{c.RuleApplied}\n" +
            (string.IsNullOrEmpty(c.LinkFileA)?"":$"LinkA:{c.LinkFileA} LinkB:{c.LinkFileB}\n")+
            "Return JSON suggestion+rfi only.";

        private static (string,string) Parse(JObject j)
        {
            var t=j["choices"]?[0]?["message"]?["content"]?.ToString()??"";
            try
            {
                t=t.Trim();
                if(t.StartsWith("```")){int s=t.IndexOf('\n')+1;int e=t.LastIndexOf("```");if(e>s)t=t.Substring(s, e-s).Trim();}
                var o=JObject.Parse(t);
                return(o["suggestion"]?.ToString()??t, o["rfi"]?.ToString()??t);
            }
            catch{return(t,t);}
        }

        private static string Rule(ClashResult c)
        {
            bool aY=(int)c.DisciplineA>(int)c.DisciplineB;
            var mv=aY?c.ElementA:c.ElementB; var st=aY?c.ElementB:c.ElementA;
            var mD=aY?c.DisciplineA:c.DisciplineB;
            double needed=Math.Abs(Math.Min(c.GapMM,0))+150;
            string link=string.IsNullOrEmpty(c.LinkFileA)?"":" [from linked model]";
            return $"Reroute {mv.Category?.Name}({mD},ID {mv.Id.Value}{link}) " +
                   $"≥{needed:F0}mm to clear {st.Category?.Name}. " +
                   $"Grid {c.GridRef}, Level {c.LevelName}. " +
                   $"Maintain ≥50mm insulation + ≥100mm maintenance.";
        }
    }
}
