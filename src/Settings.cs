// Settings.cs
using Newtonsoft.Json;
using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace ClashResolveAI
{
    public class AppSettings
    {
        public string OpenAiApiKey      { get; set; } = "";
        public string OpenAiModel       { get; set; } = "gpt-4o";
        public string OutputFolder      { get; set; } = DefaultOut();
        public double InsulationMM      { get; set; } = 50.0;
        public double MaintenanceMM     { get; set; } = 100.0;
        public bool   LiveMonitorUseAI  { get; set; } = true;
        public bool   IncludeStructural { get; set; } = true;
        public bool   AutoSectionBox    { get; set; } = true;
        public bool   ShowToast         { get; set; } = true;
        public bool   ScanLinkedModels  { get; set; } = true;

        private static string SettingsPath =>
            Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
                "ClashResolveAI_Settings.json");

        public static AppSettings Load()
        {
            try { if (File.Exists(SettingsPath)) return JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(string.Concat("[ClashResolve] Settings load error: ", ex.Message)); }
            return new AppSettings();
        }

        public static void Save(AppSettings s) =>
            File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(s, Formatting.Indented));

        private static string DefaultOut() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "ClashResolve_Reports");
    }

    public class SettingsDialog : Form
    {
        public AppSettings Settings { get; private set; }
        private TextBox _apiKey=null!, _output=null!;
        private ComboBox _model=null!;
        private NumericUpDown _insul=null!, _maint=null!;
        private CheckBox _liveAI=null!, _sectBox=null!, _toast=null!, _links=null!;

        public SettingsDialog(AppSettings s) { Settings=s; Build(); }

        private void Build()
        {
            Text="ClashResolve AI v3.1 — Settings"; Width=580;
            FormBorderStyle=FormBorderStyle.FixedDialog; MaximizeBox=false;
            StartPosition=FormStartPosition.CenterScreen;
            BackColor=Color.FromArgb(240,244,248); Font=new Font("Segoe UI",9f);

            int y=16;
            A(L("ClashResolve AI v3.1 — Settings",new Font("Segoe UI",13f,FontStyle.Bold),Color.FromArgb(13,27,46),new Point(20,y),new Size(520,28))); y+=36;

            A(S("OpenAI",y)); y+=24;
            A(L("API Key:",pt:new Point(20,y+4)));
            _apiKey=T(Settings.OpenAiApiKey,new Point(155,y),390,'*'); A(_apiKey); y+=30;
            A(L("Model:",pt:new Point(20,y+4)));
            _model=new ComboBox{Location=new Point(155,y),Size=new Size(180,26),DropDownStyle=ComboBoxStyle.DropDownList};
            _model.Items.AddRange(new[]{"gpt-4o","gpt-4o-mini","gpt-4-turbo","gpt-3.5-turbo"});
            _model.SelectedItem=Settings.OpenAiModel; if(_model.SelectedIndex<0)_model.SelectedIndex=0;
            A(_model); y+=32;

            A(S("Output Folder",y)); y+=24;
            A(L("Folder:",pt:new Point(20,y+4)));
            _output=T(Settings.OutputFolder,new Point(155,y),320); A(_output);
            var br=new Button{Text="…",Location=new Point(483,y),Size=new Size(52,26)};
            br.Click+=(_,_)=>{using var fbd=new FolderBrowserDialog{SelectedPath=_output.Text};if(fbd.ShowDialog()==DialogResult.OK)_output.Text=fbd.SelectedPath;};
            A(br); y+=32;

            A(S("Clearance (mm)",y)); y+=24;
            A(L("Insulation:",pt:new Point(20,y+4))); _insul=N((decimal)Settings.InsulationMM,new Point(155,y)); A(_insul); y+=26;
            A(L("Maintenance:",pt:new Point(20,y+4))); _maint=N((decimal)Settings.MaintenanceMM,new Point(155,y)); A(_maint); y+=32;

            A(S("Options",y)); y+=24;
            _liveAI  =CB("Use GPT-4o for live resolution suggestions",      new Point(20,y),Settings.LiveMonitorUseAI);   A(_liveAI);   y+=22;
            _sectBox =CB("Auto-zoom to clash with 3D section box",          new Point(20,y),Settings.AutoSectionBox);      A(_sectBox);  y+=22;
            _toast   =CB("Show toast notifications for new clashes",         new Point(20,y),Settings.ShowToast);           A(_toast);    y+=22;
            _links   =CB("Include linked models (Arch/Struct/MEP) in scans",new Point(20,y),Settings.ScanLinkedModels);    A(_links);    y+=30;

            var save=new Button{Text="Save",DialogResult=DialogResult.OK,Location=new Point(385,y),Size=new Size(120,34),BackColor=Color.FromArgb(13,27,46),ForeColor=Color.White,FlatStyle=FlatStyle.Flat};
            save.Click+=(_,_)=>Settings=new AppSettings{OpenAiApiKey=_apiKey.Text.Trim(),OpenAiModel=_model.SelectedItem?.ToString()??"gpt-4o",OutputFolder=_output.Text.Trim(),InsulationMM=(double)_insul.Value,MaintenanceMM=(double)_maint.Value,LiveMonitorUseAI=_liveAI.Checked,AutoSectionBox=_sectBox.Checked,ShowToast=_toast.Checked,ScanLinkedModels=_links.Checked};
            var cancel=new Button{Text="Cancel",DialogResult=DialogResult.Cancel,Location=new Point(513,y),Size=new Size(80,34)};
            A(save); A(cancel); AcceptButton=save; CancelButton=cancel;
            ClientSize=new Size(610,y+56);
        }

        private void A(Control c)=>Controls.Add(c);
        private static Label L(string t,Font? f=null,Color? c=null,Point pt=default,Size sz=default)=>new Label{Text=t,Font=f,ForeColor=c??Color.FromArgb(40,50,60),Location=pt,Size=sz==default?new Size(130,22):sz,AutoSize=sz==default};
        private static Label S(string t,int y)=>new Label{Text=t.ToUpper(),Font=new Font("Segoe UI",8.5f,FontStyle.Bold),ForeColor=Color.FromArgb(26,82,118),Location=new Point(20,y),Size=new Size(520,20)};
        private static TextBox T(string v,Point pt,int w,char p='\0')=>new TextBox{Text=v,Location=pt,Size=new Size(w,26),PasswordChar=p};
        private static NumericUpDown N(decimal v,Point pt)=>new NumericUpDown{Value=v,Minimum=1,Maximum=1000,Location=pt,Size=new Size(100,26)};
        private static CheckBox CB(string t,Point pt,bool c)=>new CheckBox{Text=t,Checked=c,Location=pt,Size=new Size(520,22)};
    }
}
