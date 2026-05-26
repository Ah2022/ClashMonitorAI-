// Reports/ExcelReportGenerator.cs
using ClosedXML.Excel;
using ClashResolveAI.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClashResolveAI.Reports
{
    public static class ExcelReportGenerator
    {
        private static readonly XLColor NavyBg  = XLColor.FromHtml("#0D1B2E");
        private static readonly XLColor White   = XLColor.White;
        private static readonly XLColor Crit    = XLColor.FromHtml("#922B21");
        private static readonly XLColor Hard    = XLColor.FromHtml("#C0392B");
        private static readonly XLColor Soft    = XLColor.FromHtml("#E67E22");
        private static readonly XLColor Clr     = XLColor.FromHtml("#F1C40F");
        private static readonly XLColor Green   = XLColor.FromHtml("#27AE60");
        private static readonly XLColor Blue    = XLColor.FromHtml("#1A5276");
        private static readonly XLColor Alt     = XLColor.FromHtml("#F5F8FA");

        public static string Generate(List<ClashResult> clashes, string project, string folder)
        {
            string path = Path.Combine(folder, $"ClashReport_{project}_{DateTime.Now:yyyyMMdd_HHmm}.xlsx");
            using var wb = new XLWorkbook();
            Summary(wb, clashes, project);
            Detail(wb, clashes);
            RFIs(wb, clashes);
            wb.SaveAs(path);
            return path;
        }

        private static void Summary(IXLWorkbook wb, List<ClashResult> cl, string proj)
        {
            var ws = wb.Worksheets.Add("Summary");
            ws.ShowGridLines = false;
            Set(ws,"B2",$"MEP CLASH REPORT — {proj}",16,true,Blue);
            Set(ws,"B3",$"Generated: {DateTime.Now:dd MMM yyyy HH:mm} | ClashResolve AI v3.1",10);

            var cards = new[]{
                ("B","TOTAL",    cl.Count,                                           Blue),
                ("D","CRITICAL", cl.Count(c=>c.Severity==ClashSeverity.Critical),   Crit),
                ("F","HARD",     cl.Count(c=>c.Severity==ClashSeverity.Hard),       Hard),
                ("H","SOFT",     cl.Count(c=>c.Severity==ClashSeverity.Soft),       Soft),
                ("J","CLEARANCE",cl.Count(c=>c.Severity==ClashSeverity.Clearance),  Clr),
                ("L","CROSS-LNK",cl.Count(c=>!string.IsNullOrEmpty(c.LinkFileA)||!string.IsNullOrEmpty(c.LinkFileB)), XLColor.FromHtml("#16A085")),
            };
            foreach(var(col,lbl,val,color) in cards){
                Set(ws,$"{col}6",lbl,9,true,color); Set(ws,$"{col}7",val.ToString(),20,true,color);
                ws.Cell($"{col}6").Style.Fill.BackgroundColor=XLColor.FromHtml("#F2F4F4");
                ws.Cell($"{col}7").Style.Fill.BackgroundColor=XLColor.FromHtml("#F2F4F4");
            }

            int r=10; Set(ws,$"B{r}","DISCIPLINE BREAKDOWN",11,true,Blue); r++;
            string[] hdrs={"Discipline Pair","Critical","Hard","Soft","Clearance","Total"};
            for(int i=0;i<hdrs.Length;i++){
                var c=ws.Cell(r,i+2); c.Value=hdrs[i];
                c.Style.Font.Bold=true; c.Style.Font.FontColor=White;
                c.Style.Fill.BackgroundColor=NavyBg;
                c.Style.Alignment.Horizontal=XLAlignmentHorizontalValues.Center;
            } r++;
            bool alt=false;
            foreach(var g in cl.GroupBy(c=>$"{c.DisciplineA} vs {c.DisciplineB}").OrderByDescending(g=>g.Count())){
                var bg=alt?Alt:XLColor.White;
                ws.Cell(r,2).Value=g.Key; ws.Cell(r,3).Value=g.Count(x=>x.Severity==ClashSeverity.Critical);
                ws.Cell(r,4).Value=g.Count(x=>x.Severity==ClashSeverity.Hard);
                ws.Cell(r,5).Value=g.Count(x=>x.Severity==ClashSeverity.Soft);
                ws.Cell(r,6).Value=g.Count(x=>x.Severity==ClashSeverity.Clearance);
                ws.Cell(r,7).Value=g.Count();
                for(int i=2;i<=7;i++) ws.Cell(r,i).Style.Fill.BackgroundColor=bg;
                alt=!alt; r++;
            }

            // Linked model breakdown
            r++; Set(ws,$"B{r}","LINKED MODEL CLASHES",11,true,Blue); r++;
            string[] lhdrs={"Link File","Critical","Hard","Soft","Total"};
            for(int i=0;i<lhdrs.Length;i++){
                var c=ws.Cell(r,i+2); c.Value=lhdrs[i]; c.Style.Font.Bold=true;
                c.Style.Font.FontColor=White; c.Style.Fill.BackgroundColor=NavyBg;
            } r++;
            var linkGroups=cl.Where(c=>!string.IsNullOrEmpty(c.LinkFileA)||!string.IsNullOrEmpty(c.LinkFileB))
                             .GroupBy(c=>string.IsNullOrEmpty(c.LinkFileA)?c.LinkFileB:c.LinkFileA);
            alt=false;
            foreach(var g in linkGroups){
                var bg=alt?Alt:XLColor.White;
                ws.Cell(r,2).Value=g.Key; ws.Cell(r,3).Value=g.Count(x=>x.Severity==ClashSeverity.Critical);
                ws.Cell(r,4).Value=g.Count(x=>x.Severity==ClashSeverity.Hard);
                ws.Cell(r,5).Value=g.Count(x=>x.Severity==ClashSeverity.Soft);
                ws.Cell(r,6).Value=g.Count();
                for(int i=2;i<=6;i++) ws.Cell(r,i).Style.Fill.BackgroundColor=bg;
                alt=!alt; r++;
            }

            ws.Column("A").Width=2; ws.Column("B").Width=28;
            for(int i=3;i<=12;i++) ws.Column(i).Width=12;
        }

        private static void Detail(IXLWorkbook wb, List<ClashResult> cl)
        {
            var ws=wb.Worksheets.Add("Clash Detail"); ws.ShowGridLines=false;
            string[] cols={"Clash ID","Priority","Severity","Gap(mm)","Elem A ID","Cat A","Disc A","Elem B ID","Cat B","Disc B","Level","Grid","Location","Rule","Link A","Link B","AI Suggestion","Status"};
            int[] widths={10,10,12,10,12,20,14,12,20,14,18,10,28,22,14,14,50,10};
            for(int i=0;i<cols.Length;i++){
                var cell=ws.Cell(1,i+1); cell.Value=cols[i]; cell.Style.Font.Bold=true;
                cell.Style.Font.FontColor=White; cell.Style.Fill.BackgroundColor=NavyBg;
                cell.Style.Alignment.WrapText=true; cell.Style.Alignment.Horizontal=XLAlignmentHorizontalValues.Center;
                ws.Column(i+1).Width=widths[i];
            }
            ws.Row(1).Height=30;
            bool alt=false;
            for(int idx=0;idx<cl.Count;idx++){
                var c=cl[idx]; int r=idx+2; var bg=alt?Alt:XLColor.White;
                ws.Cell(r,1).Value=c.ClashId; ws.Cell(r,2).Value=c.Priority; ws.Cell(r,3).Value=c.Severity.ToString();
                ws.Cell(r,4).Value=c.GapMM; ws.Cell(r,5).Value=c.ElementA.Id.Value;
                ws.Cell(r,6).Value=c.ElementA.Category?.Name??""; ws.Cell(r,7).Value=c.DisciplineA.ToString();
                ws.Cell(r,8).Value=c.ElementB.Id.Value; ws.Cell(r,9).Value=c.ElementB.Category?.Name??"";
                ws.Cell(r,10).Value=c.DisciplineB.ToString(); ws.Cell(r,11).Value=c.LevelName;
                ws.Cell(r,12).Value=c.GridRef; ws.Cell(r,13).Value=c.LocationText;
                ws.Cell(r,14).Value=c.RuleApplied; ws.Cell(r,15).Value=c.LinkFileA; ws.Cell(r,16).Value=c.LinkFileB;
                ws.Cell(r,17).Value=c.AiSuggestion; ws.Cell(r,18).Value=c.Status;
                XLColor sev; if(c.Severity==ClashSeverity.Critical)sev=Crit; else if(c.Severity==ClashSeverity.Hard)sev=Hard; else if(c.Severity==ClashSeverity.Soft)sev=Soft; else if(c.Severity==ClashSeverity.Clearance)sev=Clr; else sev=XLColor.Gray;
                ws.Cell(r,3).Style.Font.FontColor=sev; ws.Cell(r,3).Style.Font.Bold=true;
                XLColor pri; if(c.Priority=="Critical")pri=Crit; else if(c.Priority=="High")pri=Hard; else if(c.Priority=="Medium")pri=Soft; else pri=Green;
                ws.Cell(r,2).Style.Font.FontColor=pri; ws.Cell(r,2).Style.Font.Bold=true;
                // Highlight cross-link rows
                if(!string.IsNullOrEmpty(c.LinkFileA)||!string.IsNullOrEmpty(c.LinkFileB))
                    bg=XLColor.FromHtml("#E8F6F3");
                for(int i=1;i<=18;i++) ws.Cell(r,i).Style.Fill.BackgroundColor=bg;
                alt=!alt;
            }
            ws.SheetView.FreezeRows(1); ws.RangeUsed()?.SetAutoFilter();
        }

        private static void RFIs(IXLWorkbook wb, List<ClashResult> cl)
        {
            var ws=wb.Worksheets.Add("RFI Log"); ws.ShowGridLines=false;
            string[] hdrs={"RFI No.","Issued","Priority","Discipline","Level","Grid","Link Files","RFI Text","Status"};
            int[] widths={10,14,10,22,18,10,24,80,10};
            for(int i=0;i<hdrs.Length;i++){
                var cell=ws.Cell(1,i+1); cell.Value=hdrs[i]; cell.Style.Font.Bold=true;
                cell.Style.Font.FontColor=White; cell.Style.Fill.BackgroundColor=Blue;
                cell.Style.Alignment.Horizontal=XLAlignmentHorizontalValues.Center;
                ws.Column(i+1).Width=widths[i];
            }
            bool alt=false;
            for(int i=0;i<cl.Count;i++){
                var c=cl[i]; int r=i+2; var bg=alt?Alt:XLColor.White;
                ws.Cell(r,1).Value=$"RFI-{c.ClashId}"; ws.Cell(r,2).Value=c.DetectedAt.ToString("dd/MM/yyyy");
                ws.Cell(r,3).Value=c.Priority; ws.Cell(r,4).Value=$"{c.DisciplineA} vs {c.DisciplineB}";
                ws.Cell(r,5).Value=c.LevelName; ws.Cell(r,6).Value=c.GridRef;
                string links="";
                if(!string.IsNullOrEmpty(c.LinkFileA)) links+=c.LinkFileA;
                if(!string.IsNullOrEmpty(c.LinkFileB)&&c.LinkFileB!=c.LinkFileA) links+=" / "+c.LinkFileB;
                ws.Cell(r,7).Value=links; ws.Cell(r,8).Value=c.RfiText;
                ws.Cell(r,9).Value=c.Status;
                ws.Cell(r,8).Style.Alignment.WrapText=true;
                for(int j=1;j<=9;j++) ws.Cell(r,j).Style.Fill.BackgroundColor=bg;
                ws.Row(r).Height=60; alt=!alt;
            }
            ws.SheetView.FreezeRows(1); ws.RangeUsed()?.SetAutoFilter();
        }

        private static void Set(IXLWorksheet ws,string addr,string val,int sz=11,bool bold=false,XLColor? color=null){
            var c=ws.Cell(addr); c.Value=val; c.Style.Font.FontSize=sz; c.Style.Font.Bold=bold;
            if(color!=null) c.Style.Font.FontColor=color;
        }
    }
}
