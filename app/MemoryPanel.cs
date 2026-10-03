using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace Kroton
{
    /// <summary>
    /// Grafico rolante do uso de RAM (GDI+ puro, sem bibliotecas externas).
    /// Recebe amostras de porcentagem em uso e desenha a area com o gradiente do tema.
    /// </summary>
    internal class MemoryChartPanel : Panel
    {
        private const int MaxSamples = 180;   // ~6 min com amostragem de 2s
        private readonly List<float> samples = new List<float>();
        private readonly object sync = new object();
        private float peak;

        public MemoryChartPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = KrotonTheme.BgDarkest;
            BorderStyle = BorderStyle.FixedSingle;
        }

        public void AddSample(float percent)
        {
            lock (sync)
            {
                samples.Add(percent);
                if (samples.Count > MaxSamples) samples.RemoveAt(0);
                if (percent > peak) peak = percent;
            }
            Invalidate();
        }

        public void ResetPeak()
        {
            lock (sync) { peak = 0; }
        }

        /// <summary>Maior uso (%) registrado desde a abertura da aba.</summary>
        public float PeakPercent
        {
            get { lock (sync) { return peak; } }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.Clear(BackColor);

            Rectangle rect = ClientRectangle;
            int left = 44, right = 8, top = 8, bottom = 18;
            Rectangle plot = Rectangle.FromLTRB(rect.Left + left, rect.Top + top,
                rect.Right - right, rect.Bottom - bottom);

            if (plot.Width < 20 || plot.Height < 20) return;

            // Fonte CLONADA: nunca dar Dispose na fonte estatica compartilhada do
            // KrotonTheme (destrui-la derruba a GUI no proximo paint).
            using (Font font = new Font(KrotonTheme.FontSmall.FontFamily,
                KrotonTheme.FontSmall.Size, FontStyle.Regular))
            using (Pen grid = new Pen(KrotonTheme.BorderColor))
            using (Brush text = new SolidBrush(KrotonTheme.TextMuted))
            {
                try
                {
                // linhas de grade + rotulos (0/25/50/75/100%)
                for (int pct = 0; pct <= 100; pct += 25)
                {
                    int y = plot.Bottom - (int)((plot.Height * pct) / 100f);
                    g.DrawLine(grid, plot.Left, y, plot.Right, y);
                    string label = pct + "%";
                    SizeF sz = g.MeasureString(label, font);
                    g.DrawString(label, font, text, plot.Left - sz.Width - 6, y - sz.Height / 2f);
                }

                List<float> copy;
                lock (sync) { copy = new List<float>(samples); }

                if (copy.Count >= 2)
                {
                    PointF[] pts = new PointF[copy.Count];
                    for (int i = 0; i < copy.Count; i++)
                    {
                        float x = plot.Left + (plot.Width * i) / (float)(copy.Count - 1);
                        float v = Math.Max(0f, Math.Min(100f, copy[i]));
                        float y = plot.Bottom - (plot.Height * v) / 100f;
                        pts[i] = new PointF(x, y);
                    }

                    // area preenchida com o gradiente azul -> teal do tema
                    PointF[] area = new PointF[pts.Length + 2];
                    pts.CopyTo(area, 0);
                    area[area.Length - 2] = new PointF(pts[pts.Length - 1].X, plot.Bottom);
                    area[area.Length - 1] = new PointF(pts[0].X, plot.Bottom);

                    using (LinearGradientBrush areaBrush = new LinearGradientBrush(
                        plot,
                        Color.FromArgb(120, KrotonTheme.GradientStart),
                        Color.FromArgb(30, KrotonTheme.GradientEnd),
                        LinearGradientMode.Vertical))
                    {
                        g.FillPolygon(areaBrush, area);
                    }

                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (Pen line = new Pen(KrotonTheme.GradientMid, 1.8f))
                    {
                        g.DrawLines(line, pts);
                    }
                    g.SmoothingMode = SmoothingMode.None;
                }

                using (Brush cap = new SolidBrush(KrotonTheme.TextSecondary))
                {
                    float last = copy.Count > 0 ? copy[copy.Count - 1] : 0f;
                    string caption = "Uso de RAM (amostra a cada 2s)  |  atual: " +
                        last.ToString("0") + "%  |  pico: " + peak.ToString("0") + "%";
                    g.DrawString(caption, font, cap, plot.Left, plot.Bottom + 3);
                }
                }
                catch { /* GDI+ nao derruba a GUI se um frame falhar */ }
            }
        }
    }

    /// <summary>
    /// Grafico em BARRAS horizontais: uso de RAM por APLICATIVO, com o ICONE do
    /// programa dentro da barra, nome dentro da barra e valor (FormatRam / %) a direita.
    /// Barras proporcionais ao maior consumidor; valores absolutos ao lado.
    /// </summary>
    internal class MemoryBarsPanel : Panel
    {
        internal class BarItem
        {
            public string Name;
            public long Ws;
            public int Count;
            public float Pct;      // % da RAM total
            public Icon AppIcon;   // pertence ao cache do MemoryPanel - NAO dar Dispose aqui
            public string Initial; // fallback sem icone (primeira letra)
        }

        private List<BarItem> items = new List<BarItem>();
        private readonly object sync = new object();

        // Formata bytes em GB (1 casa) quando >= 1 GB; abaixo disso mantem MB para
        // nao exibir "0,0 GB" em processos pequenos. Unica fonte de unidade da aba.
        internal static string FormatRam(double bytes)
        {
            double gb = bytes / 1073741824.0;
            if (gb >= 1.0) return gb.ToString("0.0") + " GB";
            return (bytes / 1048576.0).ToString("0") + " MB";
        }

        public MemoryBarsPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = KrotonTheme.BgDark;
            BorderStyle = BorderStyle.FixedSingle;
        }

        public void SetItems(List<BarItem> src)
        {
            lock (sync) { items = src != null ? src : new List<BarItem>(); }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            List<BarItem> copy;
            lock (sync) { copy = items; }

            int headerH = 16;
            int rowH = 18;
            int gap = 4;
            int valueW = 116;
            int barW = ClientSize.Width - 8 - valueW - 8;

            // Fontes CLONADAS: nunca dar Dispose nas estaticas do KrotonTheme.
            using (Font headFont = new Font(KrotonTheme.FontSmall.FontFamily, 7.5f, FontStyle.Bold))
            using (Font rowFont = new Font(KrotonTheme.FontSmall.FontFamily, KrotonTheme.FontSmall.Size, FontStyle.Regular))
            using (Brush headBrush = new SolidBrush(KrotonTheme.TextMuted))
            using (Brush valueBrush = new SolidBrush(KrotonTheme.TextSecondary))
            using (Brush insideBrush = new SolidBrush(Color.White))
            using (Brush initialBrush = new SolidBrush(KrotonTheme.BgCardLight))
            using (StringFormat leftCenter = new StringFormat
            {
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            })
            using (StringFormat rightCenter = new StringFormat
            {
                Alignment = StringAlignment.Far,
                LineAlignment = StringAlignment.Center
            })
            {
                try
                {
                    g.DrawString("MEMÓRIA POR APLICATIVO", headFont, headBrush,
                        new RectangleF(4, 2, ClientSize.Width - 8, headerH));

                    if (copy.Count == 0 || barW < 80 || ClientSize.Height < headerH + rowH) return;

                    float maxPct = 0.01f;
                    foreach (BarItem it in copy) { if (it.Pct > maxPct) maxPct = it.Pct; }

                    int maxRows = Math.Max(0, (ClientSize.Height - headerH - 4) / (rowH + gap));
                    int n = Math.Min(copy.Count, maxRows);
                    int y = headerH + 2;

                    for (int i = 0; i < n; i++)
                    {
                        BarItem it = copy[i];
                        Rectangle bar = new Rectangle(4, y, barW, rowH);

                        // trilho arredondado
                        using (GraphicsPath tp = KrotonTheme.RoundedRect(bar, 7))
                        {
                            using (SolidBrush tb = new SolidBrush(KrotonTheme.BgCardLight)) g.FillPath(tb, tp);
                            using (Pen tpn = new Pen(KrotonTheme.BorderColor)) g.DrawPath(tpn, tp);
                        }

                        // preenchimento proporcional (escala relativa ao maior)
                        float ratio = Math.Max(0f, Math.Min(1f, it.Pct / maxPct));
                        int fillW = (int)((barW - 2) * Math.Max(0.10f, ratio));
                        if (fillW > 8)
                        {
                            Rectangle fill = new Rectangle(bar.X + 1, bar.Y + 1, fillW, rowH - 2);
                            using (GraphicsPath fp = KrotonTheme.RoundedRect(fill, 6))
                            using (LinearGradientBrush fb = new LinearGradientBrush(fill,
                                KrotonTheme.GradientStart, KrotonTheme.GradientMid,
                                LinearGradientMode.Horizontal))
                            {
                                g.FillPath(fb, fp);
                            }
                        }

                        // ICONE do aplicativo DENTRO da barra (inicio)
                        int iconSize = rowH - 4;
                        int ix = bar.X + 3;
                        int iy = bar.Y + 2;
                        if (it.AppIcon != null)
                        {
                            try { g.DrawIcon(it.AppIcon, new Rectangle(ix, iy, iconSize, iconSize)); }
                            catch { }
                        }
                        else
                        {
                            Rectangle iniRect = new Rectangle(ix, iy, iconSize, iconSize);
                            using (GraphicsPath cp = KrotonTheme.RoundedRect(iniRect, iconSize / 2))
                            {
                                g.FillPath(initialBrush, cp);
                            }
                            string ini = string.IsNullOrEmpty(it.Initial) ? "?"
                                : it.Initial.Substring(0, 1).ToUpperInvariant();
                            SizeF sz = g.MeasureString(ini, rowFont);
                            g.DrawString(ini, rowFont, headBrush,
                                iniRect.X + (iconSize - sz.Width) / 2f,
                                iniRect.Y + (iconSize - sz.Height) / 2f);
                        }

                        // nome do aplicativo dentro da barra
                        Rectangle nameRect = new Rectangle(
                            ix + iconSize + 5, bar.Y, barW - (ix - bar.X) - iconSize - 9, rowH);
                        g.DrawString(it.Name, rowFont, insideBrush, nameRect, leftCenter);

                        // valor a direita (fora da barra) - unidade via FormatRam (GB/MB)
                        string val = FormatRam(it.Ws) + "   " +
                            it.Pct.ToString("0.0") + "%";
                        Rectangle valRect = new Rectangle(bar.Right + 6, bar.Y, valueW - 8, rowH);
                        g.DrawString(val, rowFont, valueBrush, valRect, rightCenter);

                        y += rowH + gap;
                    }
                }
                catch { /* GDI+ nao derruba a GUI se um frame falhar */ }
            }
        }
    }

    /// <summary>
    /// Aba "Memoria": grafico de uso de RAM, barras por aplicativo com icone,
    /// top de processos agrupados e botao para acionar o guard (app\Manage-Memory.ps1).
    /// </summary>
    internal class MemoryPanel : UserControl
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        /// <summary>
        /// Leitura estatica do uso de RAM (reusada pelo icone da bandeja da GUI,
        /// que precisa do % sem instanciar o painel inteiro).
        /// </summary>
        public static bool TryGetUsage(out uint loadPercent, out ulong totalBytes, out ulong availBytes)
        {
            loadPercent = 0;
            totalBytes = 0;
            availBytes = 0;

            MEMORYSTATUSEX status = new MEMORYSTATUSEX();
            status.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            if (!GlobalMemoryStatusEx(ref status)) return false;

            loadPercent = status.dwMemoryLoad;
            totalBytes = status.ullTotalPhys;
            availBytes = status.ullAvailPhys;
            return true;
        }

        /// <summary>
        /// Liga/desliga a amostragem (2s): a GUI pausa quando a janela esta
        /// oculta ou a aba Memoria nao e' a ativa (alivia CPU/memoria).
        /// </summary>
        public void SetSampling(bool enabled)
        {
            if (sampleTimer == null) return;
            if (enabled)
            {
                if (!sampleTimer.Enabled)
                {
                    sampleTimer.Start();
                    Sample();
                }
            }
            else if (sampleTimer.Enabled)
            {
                sampleTimer.Stop();
            }
        }

        private MemoryChartPanel chart;
        private MemoryBarsPanel barsPanel;
        private ListView listProcs;
        private Label lblTotal;
        private Label lblUsed;
        private Label lblFree;
        private Label lblPeak;
        private Button btnFreeNow;
        private System.Windows.Forms.Timer sampleTimer;
        private bool busy;

        // Cache de icone/caminho por nome de processo (extraito uma vez, memoizado)
        private Dictionary<string, Icon> iconCache =
            new Dictionary<string, Icon>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> pathCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public MemoryPanel()
        {
            BuildLayout();
            StartSampling();
        }

        private void BuildLayout()
        {
            BackColor = KrotonTheme.BgDark;
            Dock = DockStyle.Fill;

            // ---- barra de estatisticas + botao ----
            Panel bar = new Panel();
            bar.Dock = DockStyle.Top;
            bar.Height = 70;
            bar.BackColor = KrotonTheme.BgCard;

            lblTotal = MakeStat("Total", 10);
            lblUsed = MakeStat("Em uso", 210);
            lblFree = MakeStat("Livre", 430);
            lblPeak = MakeStat("Pico", 630);

            btnFreeNow = new RoundedButton();
            btnFreeNow.Text = "Liberar memória agora";
            btnFreeNow.Location = new Point(700, 10);
            btnFreeNow.Width = 185;
            btnFreeNow.Height = 27;
            KrotonTheme.StyleButton(btnFreeNow, true);
            btnFreeNow.Click += delegate { FreeNow(); };
            bar.Controls.Add(btnFreeNow);

            bar.Controls.Add(lblTotal);
            bar.Controls.Add(lblUsed);
            bar.Controls.Add(lblFree);
            bar.Controls.Add(lblPeak);
            KrotonTheme.RoundCorners(bar, 8);

            // ---- grafico de linha ----
            chart = new MemoryChartPanel();
            chart.Dock = DockStyle.Top;
            chart.Height = 160;
            KrotonTheme.RoundCorners(chart, 8);

            // ---- barras por aplicativo (icone dentro da barra) ----
            barsPanel = new MemoryBarsPanel();
            barsPanel.Dock = DockStyle.Top;
            barsPanel.Height = 196;
            KrotonTheme.RoundCorners(barsPanel, 8);

            // ---- top de processos ----
            listProcs = new ListView();
            listProcs.Dock = DockStyle.Fill;
            listProcs.View = View.Details;
            listProcs.FullRowSelect = true;
            listProcs.BackColor = KrotonTheme.BgDarkest;
            listProcs.ForeColor = KrotonTheme.TextPrimary;
            listProcs.Font = KrotonTheme.FontBody;
            listProcs.BorderStyle = BorderStyle.None;
            listProcs.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            listProcs.Columns.Add("Aplicativo", 220);
            listProcs.Columns.Add("Instâncias", 70);
            listProcs.Columns.Add("Memória", 110);
            listProcs.Columns.Add("% da RAM", 80);

            // ---- ordem de Dock: o ultimo adicionado e ancorado primeiro ----
            // (fill primeiro; depois barsPanel/chart/barra no topo, nessa ordem)
            KrotonTheme.RoundCorners(listProcs, 8);
            Controls.Add(listProcs);
            Controls.Add(barsPanel);
            Controls.Add(chart);
            Controls.Add(bar);
        }

        private Label MakeStat(string title, int x)
        {
            Label l = new Label();
            l.Text = title + "\n-";
            l.Location = new Point(x, 8);
            l.AutoSize = true;
            l.Font = KrotonTheme.FontSmall;
            l.ForeColor = KrotonTheme.TextSecondary;
            l.Tag = title;
            return l;
        }

        private static void SetStat(Label l, string value)
        {
            l.Text = l.Tag + "\n" + value;
        }

        private void StartSampling()
        {
            sampleTimer = new System.Windows.Forms.Timer();
            sampleTimer.Interval = 2000;
            sampleTimer.Tick += delegate { Sample(); };
            sampleTimer.Start();
            Sample();
        }

        private class ProcRow
        {
            public string Name;
            public int Count;      // instancias (ex.: 14 processos chrome)
            public long Ws;        // soma do working set de todas as instancias
            public int SamplePid;  // pid de referencia p/ extrair o icone do exe
        }

        /// <summary>
        /// Extrai o icone do executavel UMA vez por nome de processo (caminho e
        /// resultado memoizados - inclusive falhas). Chamado so para o top das barras.
        /// </summary>
        private Icon GetAppIcon(string procName, int pid)
        {
            Icon ic;
            if (iconCache.TryGetValue(procName, out ic)) return ic;

            string path;
            if (!pathCache.TryGetValue(procName, out path))
            {
                path = "";
                try
                {
                    using (Process p = Process.GetProcessById(pid))
                    {
                        path = p.MainModule.FileName;
                    }
                }
                catch { }
                pathCache[procName] = path;
            }

            if (path.Length > 0)
            {
                try { ic = Icon.ExtractAssociatedIcon(path); }
                catch { ic = null; }
            }
            iconCache[procName] = ic;
            return ic;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (Icon ic in iconCache.Values)
                {
                    if (ic != null) { try { ic.Dispose(); } catch { } }
                }
                iconCache.Clear();
            }
            base.Dispose(disposing);
        }

        private void Sample()
        {
            if (IsDisposed) return;

            MEMORYSTATUSEX status = new MEMORYSTATUSEX();
            status.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            if (!GlobalMemoryStatusEx(ref status)) return;

            double totalGb = status.ullTotalPhys / 1073741824.0;
            double usedGb = (status.ullTotalPhys - status.ullAvailPhys) / 1073741824.0;
            double freeGb = status.ullAvailPhys / 1073741824.0;

            SetStat(lblTotal, totalGb.ToString("0.0") + " GB");
            SetStat(lblUsed, usedGb.ToString("0.0") + " GB (" + status.dwMemoryLoad + "%)");
            SetStat(lblFree, freeGb.ToString("0.0") + " GB");
            SetStat(lblPeak, chart.PeakPercent.ToString("0") + "%");

            chart.AddSample(status.dwMemoryLoad);

            try
            {
                // Agrupa por APLICATIVO: soma a memoria de todas as instancias
                // do mesmo executavel (ex.: chrome.exe x14 = uma linha so).
                Dictionary<string, ProcRow> byApp =
                    new Dictionary<string, ProcRow>(StringComparer.OrdinalIgnoreCase);
                foreach (Process p in Process.GetProcesses())
                {
                    long ws = 0;
                    try { ws = p.WorkingSet64; }
                    catch { }
                    if (ws > 0)
                    {
                        ProcRow row;
                        if (!byApp.TryGetValue(p.ProcessName, out row))
                        {
                            row = new ProcRow { Name = p.ProcessName, SamplePid = p.Id };
                            byApp[p.ProcessName] = row;
                        }
                        row.Ws += ws;
                        row.Count++;
                    }
                    try { p.Dispose(); }
                    catch { }
                }

                List<ProcRow> rows = new List<ProcRow>(byApp.Values);
                rows.Sort(delegate(ProcRow a, ProcRow b) { return b.Ws.CompareTo(a.Ws); });

                // ---- barras por aplicativo (top 8) com icone do exe ----
                int barTake = Math.Min(8, rows.Count);
                List<MemoryBarsPanel.BarItem> bars =
                    new List<MemoryBarsPanel.BarItem>(barTake);
                for (int i = 0; i < barTake; i++)
                {
                    ProcRow r = rows[i];
                    double pctBar = status.ullTotalPhys > 0
                        ? (r.Ws * 100.0) / status.ullTotalPhys
                        : 0;
                    bars.Add(new MemoryBarsPanel.BarItem
                    {
                        Name = r.Name,
                        Ws = r.Ws,
                        Count = r.Count,
                        Pct = (float)pctBar,
                        AppIcon = GetAppIcon(r.Name, r.SamplePid),
                        Initial = r.Name
                    });
                }
                barsPanel.SetItems(bars);

                listProcs.BeginUpdate();
                listProcs.Items.Clear();
                int take = Math.Min(12, rows.Count);
                for (int i = 0; i < take; i++)
                {
                    ProcRow r = rows[i];
                    double pct = status.ullTotalPhys > 0
                        ? (r.Ws * 100.0) / status.ullTotalPhys
                        : 0;
                    ListViewItem item = new ListViewItem(r.Name);
                    item.SubItems.Add(r.Count.ToString());
                    item.SubItems.Add(MemoryBarsPanel.FormatRam(r.Ws));
                    item.SubItems.Add(pct.ToString("0.0"));
                    listProcs.Items.Add(item);
                }
                listProcs.EndUpdate();
            }
            catch { }
        }

        private void FreeNow()
        {
            if (busy) return;
            busy = true;
            btnFreeNow.Enabled = false;
            btnFreeNow.Text = "Liberando...";

            Thread t = new Thread(delegate()
            {
                string error = null;
                int freed = 0, count = 0;
                try
                {
                    string script = Path.Combine(ScriptRunner.BaseDir, @"app\Manage-Memory.ps1");
                    if (!File.Exists(script))
                        throw new InvalidOperationException("Script não encontrado:\n" + script);

                    int exit;
                    string output = ScriptRunner.RunPs("& '" + script + "' -Mode Guard -Force", 90000, out exit);
                    if (exit != 0)
                        throw new InvalidOperationException("Falha ao liberar memória:\n" + ScriptRunner.Tail(output, 5));

                    Match m = Regex.Match(output, "MEM_FREED_MB=(\\d+);MEM_PROCS=(\\d+)");
                    if (m.Success)
                    {
                        int.TryParse(m.Groups[1].Value, out freed);
                        int.TryParse(m.Groups[2].Value, out count);
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                if (IsDisposed) return;
                try
                {
                    Invoke((MethodInvoker)delegate
                    {
                        busy = false;
                        btnFreeNow.Enabled = true;
                        btnFreeNow.Text = "Liberar memória agora";

                        if (error != null)
                        {
                            MessageBox.Show(error, "Memória", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        else
                        {
                            MessageBox.Show(
                                string.Format("{0} liberados em {1} processo(s).\n\n" +
                                    "Nunca são tocados: jogo ativo, Discord/Spotify, processos com áudio ativo, " +
                                    "processo principal dos navegadores e processos críticos do Windows.",
                                    MemoryBarsPanel.FormatRam(freed * 1048576.0), count),
                                "Memória", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        Sample();
                    });
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}


