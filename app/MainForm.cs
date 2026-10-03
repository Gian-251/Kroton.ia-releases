using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Kroton
{
    internal class ServiceCatalogItem
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string Status { get; set; }
        public string StartMode { get; set; }
        public string Category { get; set; }
        public bool IsCritical { get; set; }
        public string Reason { get; set; }
    }

    public class MainForm : Form
    {
        private Panel topPanel;
        private Label lblMode;
        private Label lblMonitor;
        private Button btnRefresh;
        private ComboBox cmbOperationMode;
        private Label lblOpModeTitle;
        private Label lblModeHint;

        // Aba Servicos
        private DataGridView gridServices;
        private TextBox txtSearchService;
        private ComboBox cmbFilterCategory;
        private Button btnReloadServices;
        private Button btnSearchOnline;
        private Label lblServiceStats;
        private List<ServiceCatalogItem> allServices = new List<ServiceCatalogItem>();

        // Aba Otimizacao
        private ListView listAudit;
        private Button btnAudit;
        private Button btnApplyGame;
        private Button btnRollback;
        private TextBox txtAuditSummary;

        // Aba Jogos
        private ListBox listGames;
        private TextBox txtNewGame;
        private Button btnAddGame;
        private Button btnRemoveGame;
        private Button btnSaveGames;
        private Button btnDetectGames;
        private Button btnExportGames;
        private Button btnImportGames;

        // Aba Memoria
        private MemoryPanel memoryPanel;

        // Aba Log
        private TextBox txtLog;
        private Button btnRefreshLog;

        private System.Windows.Forms.Timer statusTimer;
        private List<string> games = new List<string>();
        private bool isUpdatingMode = false;

        // Abas (referencias para pausar a amostragem de memoria)
        private TabControl tabs;
        private TabPage memoryTab;

        // Aba "Modo Jogo" (lista de servicos parados no modo jogo)
        private TabPage gameModeTab;
        private ListView listStopped;
        private Label lblGameStatus;
        private Label lblGameDetail;

        // Menu principal e bandeja (tray)
        private MenuStrip mainMenu;
        private ToolStripMenuItem mnuInstall;
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        private System.Windows.Forms.Timer trayTimer;
        private bool reallyExit;
        private bool trayBalloonShown;

        // Status: checagem da tarefa via PowerShell e contagem de servicos parados
        private int statusTick;
        private bool forceTaskCheck = true;
        private string lastModeKey = "";

        // Menu de perfil por jogo (clique direito / duplo clique na lista)
        private ContextMenuStrip gameMenu;

        public MainForm()
        {
            BuildUi();
            LoadAll();
            StartTimer();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ReanchorLayout();
            SetSamplingState();
            GitHubUpdater.StartIfEligible(ScriptRunner.BaseDir, delegate
            {
                if (IsDisposed) return;
                try { BeginInvoke((MethodInvoker)delegate { ExitApp(); }); }
                catch (InvalidOperationException) { }
            });
        }

        // Minimizar esconde a janela e deixa a GUI na bandeja (rodando em segundo
        // plano); restaurar via duplo clique ou menu do icone.
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized && !reallyExit && Visible)
            {
                HideToTray();
            }
        }

        // Fechar (X) tambem vai para a bandeja; saida real so pelo menu
        // "Kroton.ia > Sair" ou "Sair" do menu da bandeja.
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            if (trayTimer != null) trayTimer.Stop();
            if (statusTimer != null) statusTimer.Stop();
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                Icon ic = trayIcon.Icon;
                trayIcon.Dispose();
                trayIcon = null;
                if (ic != null) { try { ic.Dispose(); } catch { } }
            }
            base.OnFormClosing(e);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            SetSamplingState();
            if (statusTimer != null)
            {
                // Oculta: status a cada 30s (barato); visivel: 5s como antes.
                statusTimer.Interval = Visible ? 5000 : 30000;
            }
        }

        // As ancoragens a direita gravaram seus offsets quando os pais ainda tinham
        // o tamanho padrao do Panel (200px), o que jogava "Comportamento",
        // "Atualizar" e o resumo da auditoria para fora da janela.
        // Este metodo reposiciona e regrava as ancoragens com o tamanho real dos pais.
        private void ReanchorLayout()
        {
            int pw = topPanel.ClientSize.Width;

            lblOpModeTitle.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            cmbOperationMode.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            btnRefresh.Anchor = AnchorStyles.Top | AnchorStyles.Left;

            lblOpModeTitle.Location = new Point(pw - 304, 18);
            cmbOperationMode.Location = new Point(pw - 304, 38);
            btnRefresh.Location = new Point(pw - 104, 36);

            lblOpModeTitle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            cmbOperationMode.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnRefresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            lblModeHint.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            lblModeHint.Location = new Point(pw - 304, 60);
            lblModeHint.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            if (txtAuditSummary != null && txtAuditSummary.Parent != null)
            {
                txtAuditSummary.Anchor = AnchorStyles.Top | AnchorStyles.Left;
                txtAuditSummary.Location = new Point(480, 12);
                txtAuditSummary.Width = 350;
                txtAuditSummary.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            }
        }

        private void BuildUi()
        {
            Text = "Kroton.ia - Game Mode Optimizer";
            Width = 940;
            Height = 720;
            MinimumSize = new Size(820, 520);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = KrotonTheme.BgDark;
            ForeColor = KrotonTheme.TextPrimary;
            ApplyAppIcon();

            // Topo com gradiente
            topPanel = new Panel();
            topPanel.Dock = DockStyle.Top;
            topPanel.Height = 84;
            topPanel.BackColor = KrotonTheme.BgDarkest;
            topPanel.Paint += delegate(object s, PaintEventArgs e) {
                KrotonTheme.PaintGradientHeader(e.Graphics, topPanel.ClientRectangle);
            };

            PictureBox logo = new PictureBox();
            logo.Image = LoadLogo();
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            logo.Location = new Point(14, 14);
            logo.Size = new Size(56, 56);
            logo.BackColor = Color.Transparent;
            topPanel.Controls.Add(logo);

            lblMode = new Label();
            lblMode.Text = "MODO: CARREGANDO...";
            lblMode.Font = KrotonTheme.FontTitle;
            lblMode.ForeColor = Color.White;
            lblMode.Location = new Point(80, 16);
            lblMode.AutoSize = true;
            lblMode.BackColor = Color.Transparent;
            topPanel.Controls.Add(lblMode);

            lblMonitor = new Label();
            lblMonitor.Text = "Monitor: verificando...";
            lblMonitor.Font = KrotonTheme.FontSubtitle;
            lblMonitor.ForeColor = KrotonTheme.TextSecondary;
            lblMonitor.Location = new Point(82, 48);
            lblMonitor.AutoSize = true;
            lblMonitor.BackColor = Color.Transparent;
            topPanel.Controls.Add(lblMonitor);

            lblOpModeTitle = new Label();
            lblOpModeTitle.Text = "Comportamento:";
            lblOpModeTitle.Font = KrotonTheme.FontSmall;
            lblOpModeTitle.ForeColor = KrotonTheme.TextSecondary;
            lblOpModeTitle.Location = new Point(620, 18);
            lblOpModeTitle.AutoSize = true;
            lblOpModeTitle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblOpModeTitle.BackColor = Color.Transparent;
            topPanel.Controls.Add(lblOpModeTitle);

            cmbOperationMode = new ComboBox();
            cmbOperationMode.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbOperationMode.Items.Add("Automático (Modo Jogo)");
            cmbOperationMode.Items.Add("Modo Leve (Apenas Telemetria)");
            cmbOperationMode.Items.Add("Desativado (Apenas Monitorar)");
            cmbOperationMode.SelectedIndex = 0;
            cmbOperationMode.Location = new Point(620, 38);
            cmbOperationMode.Width = 190;
            KrotonTheme.RoundCorners(cmbOperationMode, 8);
            cmbOperationMode.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            cmbOperationMode.SelectedIndexChanged += delegate { OnOperationModeChanged(); };
            topPanel.Controls.Add(cmbOperationMode);

            // Descricao curta do modo selecionado (atualizada em UpdateModeHint)
            lblModeHint = new Label();
            lblModeHint.Font = KrotonTheme.FontSmall;
            lblModeHint.ForeColor = KrotonTheme.TextSecondary;
            lblModeHint.BackColor = Color.Transparent;
            lblModeHint.AutoSize = false;
            lblModeHint.TextAlign = ContentAlignment.MiddleLeft;
            lblModeHint.Location = new Point(620, 60);
            lblModeHint.Size = new Size(296, 22);
            topPanel.Controls.Add(lblModeHint);

            btnRefresh = new RoundedButton();
            btnRefresh.Text = "Atualizar";
            btnRefresh.Location = new Point(820, 36);
            btnRefresh.Width = 90;
            btnRefresh.Height = 27;
            btnRefresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            KrotonTheme.StyleButton(btnRefresh, false);
            btnRefresh.Click += delegate { LoadAll(); };
            topPanel.Controls.Add(btnRefresh);

            tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.Padding = new Point(14, 6);
            tabs.Font = KrotonTheme.FontBodyBold;

            tabs.TabPages.Add(BuildServicesTab());
            tabs.TabPages.Add(BuildOptimizationTab());
            tabs.TabPages.Add(BuildGamesTab());
            memoryTab = BuildMemoryTab();
            tabs.TabPages.Add(memoryTab);
            tabs.TabPages.Add(BuildGameModeTab());
            tabs.TabPages.Add(BuildLogTab());
            tabs.SelectedIndexChanged += delegate { SetSamplingState(); };

            BuildAppMenu();
            InitTray();

            Controls.Add(tabs);
            Controls.Add(topPanel);
            // MenuStrip adicionado por ULTIMO = mais externo na ordem de dock
            // (fica acima do cabecalho; a GUI e' o unico form, entao o layout e' estavel)
            Controls.Add(mainMenu);
        }

        private TabPage BuildServicesTab()
        {
            TabPage tab = new TabPage("Serviços do Windows");

            Panel bar = new Panel();
            bar.Dock = DockStyle.Top;
            bar.Height = 48;
            bar.BackColor = KrotonTheme.BgCard;

            Label lblFiltro = new Label();
            lblFiltro.Text = "Buscar:";
            lblFiltro.Location = new Point(10, 15);
            lblFiltro.AutoSize = true;
            lblFiltro.ForeColor = KrotonTheme.TextSecondary;
            bar.Controls.Add(lblFiltro);

            txtSearchService = new TextBox();
            txtSearchService.Location = new Point(62, 12);
            txtSearchService.Width = 160;
            txtSearchService.TextChanged += delegate { FilterServices(); };
            KrotonTheme.RoundCorners(txtSearchService, 8);
            bar.Controls.Add(txtSearchService);

            Label lblCat = new Label();
            lblCat.Text = "Categoria:";
            lblCat.Location = new Point(230, 15);
            lblCat.AutoSize = true;
            lblCat.ForeColor = KrotonTheme.TextSecondary;
            bar.Controls.Add(lblCat);

            cmbFilterCategory = new ComboBox();
            cmbFilterCategory.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbFilterCategory.Items.AddRange(new object[] { "Todas", "Sistema Protegido", "Windows (Opcional)", "Aplicativos / Terceiros" });
            cmbFilterCategory.SelectedIndex = 0;
            cmbFilterCategory.Location = new Point(295, 12);
            cmbFilterCategory.Width = 150;
            cmbFilterCategory.SelectedIndexChanged += delegate { FilterServices(); };
            KrotonTheme.RoundCorners(cmbFilterCategory, 8);
            bar.Controls.Add(cmbFilterCategory);

            btnReloadServices = new RoundedButton();
            btnReloadServices.Text = "Recarregar";
            btnReloadServices.Location = new Point(455, 10);
            btnReloadServices.Width = 90;
            btnReloadServices.Height = 27;
            btnReloadServices.Font = new Font(FontFamily.GenericSansSerif, 8F);
            KrotonTheme.StyleButton(btnReloadServices, false);
            btnReloadServices.Click += delegate { LoadServicesCatalog(); };
            bar.Controls.Add(btnReloadServices);

            btnSearchOnline = new RoundedButton();
            btnSearchOnline.Text = "Buscar descrições na Web";
            btnSearchOnline.Location = new Point(552, 10);
            btnSearchOnline.Width = 180;
            btnSearchOnline.Height = 27;
            btnSearchOnline.Font = new Font(FontFamily.GenericSansSerif, 8F);
            KrotonTheme.StyleButton(btnSearchOnline, true);
            btnSearchOnline.Click += delegate { FetchMissingDescriptionsOnline(); };
            bar.Controls.Add(btnSearchOnline);

            lblServiceStats = new Label();
            lblServiceStats.Text = "Carregando...";
            lblServiceStats.Location = new Point(740, 15);
            lblServiceStats.AutoSize = true;
            lblServiceStats.ForeColor = KrotonTheme.TextSecondary;
            bar.Controls.Add(lblServiceStats);

            gridServices = BuildServicesGrid();
            tab.Controls.Add(gridServices);
            tab.Controls.Add(bar);
            return tab;
        }

        private DataGridView BuildServicesGrid()
        {
            var g = new DataGridView();
            g.Dock = DockStyle.Fill;
            g.AllowUserToAddRows = false;
            g.AllowUserToDeleteRows = false;
            g.ReadOnly = false;
            g.RowHeadersVisible = false;
            g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            g.MultiSelect = false;
            g.BackgroundColor = KrotonTheme.BgDark;
            g.BorderStyle = BorderStyle.None;
            g.GridColor = KrotonTheme.BorderColor;
            g.EnableHeadersVisualStyles = false;
            g.RowTemplate.Height = 28;

            g.ColumnHeadersDefaultCellStyle.BackColor = KrotonTheme.BgCard;
            g.ColumnHeadersDefaultCellStyle.ForeColor = KrotonTheme.TextPrimary;
            g.ColumnHeadersDefaultCellStyle.Font = KrotonTheme.FontBodyBold;
            g.ColumnHeadersHeight = 32;

            g.DefaultCellStyle.BackColor = KrotonTheme.BgDark;
            g.DefaultCellStyle.ForeColor = KrotonTheme.TextPrimary;
            g.DefaultCellStyle.SelectionBackColor = KrotonTheme.BgCardLight;
            g.DefaultCellStyle.SelectionForeColor = Color.White;
            g.DefaultCellStyle.Font = KrotonTheme.FontBody;

            var colName = new DataGridViewTextBoxColumn();
            colName.HeaderText = "Serviço";
            colName.Name = "colName";
            colName.Width = 130;
            colName.ReadOnly = true;
            g.Columns.Add(colName);

            var colDesc = new DataGridViewTextBoxColumn();
            colDesc.HeaderText = "O que ele faz";
            colDesc.Name = "colDesc";
            colDesc.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            colDesc.ReadOnly = true;
            g.Columns.Add(colDesc);

            var colCat = new DataGridViewTextBoxColumn();
            colCat.HeaderText = "Categoria";
            colCat.Name = "colCat";
            colCat.Width = 140;
            colCat.ReadOnly = true;
            g.Columns.Add(colCat);

            var colStatus = new DataGridViewTextBoxColumn();
            colStatus.HeaderText = "Estado";
            colStatus.Name = "colStatus";
            colStatus.Width = 85;
            colStatus.ReadOnly = true;
            g.Columns.Add(colStatus);

            var colBoot = new DataGridViewButtonColumn();
            colBoot.HeaderText = "Inicialização";
            colBoot.Name = "colBoot";
            colBoot.Width = 115;
            colBoot.FlatStyle = FlatStyle.Flat;
            g.Columns.Add(colBoot);

            var colAction = new DataGridViewButtonColumn();
            colAction.HeaderText = "Ação";
            colAction.Name = "colAction";
            colAction.Width = 100;
            colAction.FlatStyle = FlatStyle.Flat;
            g.Columns.Add(colAction);

            g.CellContentClick += OnGridCellContentClick;
            g.CellFormatting += OnGridCellFormatting;
            g.CellPainting += OnGridCellPainting;
            KrotonTheme.RoundCorners(g, 8);
            return g;
        }

        // Celhas-botao (Acao / Inicializacao) como PILL arredondada colorida -
        // visual moderno no lugar do botao quadrado padrao do DataGridView.
        private void OnGridCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.Value == null) return;
            string colName = gridServices.Columns[e.ColumnIndex].Name;
            if (colName != "colAction" && colName != "colBoot") return;

            ServiceCatalogItem item = gridServices.Rows[e.RowIndex].Tag as ServiceCatalogItem;
            if (item == null) return;

            try
            {
                // fundo, borda e selecao sem o conteudo (texto/padrao quadrado)
                e.Paint(e.ClipBounds,
                    DataGridViewPaintParts.All & ~DataGridViewPaintParts.ContentForeground);

                Rectangle pill = e.CellBounds;
                pill.Inflate(-5, -4);
                if (pill.Width < 12 || pill.Height < 6) return;

                string text = e.Value.ToString();
                Color fill;
                if (item.IsCritical)
                    fill = Color.FromArgb(150, KrotonTheme.AccentLocked);
                else if (colName == "colAction")
                    fill = text == "Parar"
                        ? Color.FromArgb(205, KrotonTheme.AccentDanger)
                        : Color.FromArgb(205, KrotonTheme.AccentSuccess);
                else if (text == "Automático")
                    fill = Color.FromArgb(170, KrotonTheme.GradientStart);
                else if (text == "Desativado")
                    fill = Color.FromArgb(170, KrotonTheme.AccentDanger);
                else
                    fill = Color.FromArgb(150, KrotonTheme.BgCardLight);

                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath path = KrotonTheme.RoundedRect(pill, 7))
                {
                    using (SolidBrush b = new SolidBrush(fill)) e.Graphics.FillPath(b, path);
                    using (Pen p = new Pen(Color.FromArgb(80, Color.White))) e.Graphics.DrawPath(p, path);
                }

                Font f = e.CellStyle.Font != null ? e.CellStyle.Font : KrotonTheme.FontBodyBold;
                TextRenderer.DrawText(e.Graphics, text, f, pill, Color.White,
                    TextFormatFlags.HorizontalCenter |
                    TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis);
                e.Handled = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("Unable to read registered Kroton install path: " + ex.Message);
            }
        }

        private TabPage BuildOptimizationTab()
        {
            TabPage tab = new TabPage("Otimização / Auditoria");

            Panel bar = new Panel();
            bar.Dock = DockStyle.Top;
            bar.Height = 48;
            bar.BackColor = KrotonTheme.BgCard;

            btnAudit = new RoundedButton();
            btnAudit.Text = "Auditar candidatos";
            btnAudit.Location = new Point(10, 10);
            btnAudit.Width = 145;
            btnAudit.Height = 27;
            KrotonTheme.StyleButton(btnAudit, false);
            btnAudit.Click += delegate { RunAudit(); };
            bar.Controls.Add(btnAudit);

            btnApplyGame = new RoundedButton();
            btnApplyGame.Text = "Aplicar Modo Jogo";
            btnApplyGame.Location = new Point(165, 10);
            btnApplyGame.Width = 145;
            btnApplyGame.Height = 27;
            KrotonTheme.StyleButton(btnApplyGame, true);
            btnApplyGame.Click += delegate { ApplyMode("game"); };
            bar.Controls.Add(btnApplyGame);

            btnRollback = new RoundedButton();
            btnRollback.Text = "Restaurar Original";
            btnRollback.Location = new Point(320, 10);
            btnRollback.Width = 145;
            btnRollback.Height = 27;
            KrotonTheme.StyleButton(btnRollback, false);
            btnRollback.Click += delegate { ApplyMode("normal"); };
            bar.Controls.Add(btnRollback);

            txtAuditSummary = new TextBox();
            txtAuditSummary.Location = new Point(480, 12);
            txtAuditSummary.Width = 350;
            txtAuditSummary.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            txtAuditSummary.ReadOnly = true;
            txtAuditSummary.BackColor = KrotonTheme.BgDark;
            txtAuditSummary.ForeColor = KrotonTheme.TextPrimary;
            txtAuditSummary.BorderStyle = BorderStyle.FixedSingle;
            KrotonTheme.RoundCorners(txtAuditSummary, 8);
            bar.Controls.Add(txtAuditSummary);

            listAudit = new ListView();
            listAudit.Dock = DockStyle.Fill;
            listAudit.View = View.Details;
            listAudit.FullRowSelect = true;
            listAudit.GridLines = true;
            listAudit.BackColor = KrotonTheme.BgDark;
            listAudit.ForeColor = KrotonTheme.TextPrimary;
            listAudit.Font = KrotonTheme.FontBody;
            listAudit.BorderStyle = BorderStyle.None;
            listAudit.Columns.Add("Serviço", 160);
            listAudit.Columns.Add("Descrição", 300);
            listAudit.Columns.Add("Atual", 90);
            listAudit.Columns.Add("Alvo", 90);
            listAudit.Columns.Add("Confiança", 80);
            KrotonTheme.RoundCorners(listAudit, 8);

            tab.Controls.Add(listAudit);
            tab.Controls.Add(bar);
            return tab;
        }

        private TabPage BuildGamesTab()
        {
            TabPage tab = new TabPage("Jogos Monitorados");

            Panel bar = new Panel();
            bar.Dock = DockStyle.Top;
            bar.Height = 48;
            bar.BackColor = KrotonTheme.BgCard;

            txtNewGame = new TextBox();
            txtNewGame.Location = new Point(10, 12);
            txtNewGame.Width = 260;
            KrotonTheme.RoundCorners(txtNewGame, 8);
            bar.Controls.Add(txtNewGame);

            btnAddGame = new RoundedButton();
            btnAddGame.Text = "Adicionar";
            btnAddGame.Location = new Point(280, 10);
            btnAddGame.Width = 100;
            btnAddGame.Height = 27;
            KrotonTheme.StyleButton(btnAddGame, false);
            btnAddGame.Click += delegate { AddGame(); };
            bar.Controls.Add(btnAddGame);

            btnRemoveGame = new RoundedButton();
            btnRemoveGame.Text = "Remover";
            btnRemoveGame.Location = new Point(390, 10);
            btnRemoveGame.Width = 100;
            btnRemoveGame.Height = 27;
            KrotonTheme.StyleButton(btnRemoveGame, false);
            btnRemoveGame.Click += delegate { RemoveGame(); };
            bar.Controls.Add(btnRemoveGame);

            btnSaveGames = new RoundedButton();
            btnSaveGames.Text = "Salvar lista";
            btnSaveGames.Location = new Point(500, 10);
            btnSaveGames.Width = 110;
            btnSaveGames.Height = 27;
            KrotonTheme.StyleButton(btnSaveGames, true);
            btnSaveGames.Click += delegate { SaveGames(); };
            bar.Controls.Add(btnSaveGames);

            btnDetectGames = new RoundedButton();
            btnDetectGames.Text = "Detectar jogos";
            btnDetectGames.Location = new Point(620, 10);
            btnDetectGames.Width = 130;
            btnDetectGames.Height = 27;
            KrotonTheme.StyleButton(btnDetectGames, false);
            btnDetectGames.Click += delegate { DetectGames(); };
            bar.Controls.Add(btnDetectGames);

            // Exportar/importar a lista (backup da lista ou levar para outra maquina)
            btnExportGames = new RoundedButton();
            btnExportGames.Text = "Exportar";
            btnExportGames.Location = new Point(760, 10);
            btnExportGames.Width = 65;
            btnExportGames.Height = 27;
            KrotonTheme.StyleButton(btnExportGames, false);
            btnExportGames.Click += delegate { ExportGames(); };
            bar.Controls.Add(btnExportGames);

            btnImportGames = new RoundedButton();
            btnImportGames.Text = "Importar";
            btnImportGames.Location = new Point(835, 10);
            btnImportGames.Width = 65;
            btnImportGames.Height = 27;
            KrotonTheme.StyleButton(btnImportGames, false);
            btnImportGames.Click += delegate { ImportGames(); };
            bar.Controls.Add(btnImportGames);

            listGames = new ListBox();
            listGames.Dock = DockStyle.Fill;
            listGames.BackColor = KrotonTheme.BgDark;
            listGames.ForeColor = KrotonTheme.TextPrimary;
            listGames.Font = KrotonTheme.FontBody;
            listGames.BorderStyle = BorderStyle.None;
            KrotonTheme.RoundCorners(listGames, 8);

            // Menu de "perfil por jogo" (clique direito / duplo clique)
            gameMenu = new ContextMenuStrip();
            gameMenu.ShowImageMargin = false;
            gameMenu.Opening += delegate(object s2, System.ComponentModel.CancelEventArgs ce) { BuildGameProfileMenu(); };
            listGames.ContextMenuStrip = gameMenu;
            listGames.MouseDown += delegate(object s2, MouseEventArgs me)
            {
                if (me.Button == MouseButtons.Right)
                {
                    int idx = listGames.IndexFromPoint(me.Location);
                    if (idx >= 0) listGames.SelectedIndex = idx;
                }
            };
            listGames.DoubleClick += delegate
            {
                if (listGames.SelectedIndex >= 0)
                    gameMenu.Show(listGames, listGames.PointToClient(Cursor.Position));
            };

            tab.Controls.Add(listGames);
            tab.Controls.Add(bar);
            return tab;
        }

        /// <summary>
        /// Aba Memoria: grafico de uso de RAM, top de processos e botao que aciona
        /// app\Manage-Memory.ps1 (o mesmo guard que o runner usa no modo jogo).
        /// </summary>
        private TabPage BuildMemoryTab()
        {
            TabPage tab = new TabPage("Memória");
            memoryPanel = new MemoryPanel();
            memoryPanel.Dock = DockStyle.Fill;
            tab.Controls.Add(memoryPanel);
            return tab;
        }

        // Aba "Modo Jogo": lista dos servicos mantidos parados durante o modo jogo
        // (servicesStoppedNames gravados pelo runner na entrada do modo jogo).
        private TabPage BuildGameModeTab()
        {
            TabPage tab = new TabPage("Modo Jogo");
            tab.BackColor = KrotonTheme.BgDark;
            gameModeTab = tab;

            Panel bar = new Panel();
            bar.Dock = DockStyle.Top;
            bar.Height = 74;
            bar.BackColor = KrotonTheme.BgCard;
            KrotonTheme.RoundCorners(bar, 8);

            lblGameStatus = new Label();
            lblGameStatus.Location = new Point(14, 12);
            lblGameStatus.Size = new Size(880, 26);
            lblGameStatus.Font = KrotonTheme.FontTitle;
            lblGameStatus.Text = "—";
            bar.Controls.Add(lblGameStatus);

            lblGameDetail = new Label();
            lblGameDetail.Location = new Point(14, 44);
            lblGameDetail.Size = new Size(880, 20);
            lblGameDetail.Font = KrotonTheme.FontBody;
            lblGameDetail.ForeColor = KrotonTheme.TextSecondary;
            bar.Controls.Add(lblGameDetail);

            listStopped = new ListView();
            listStopped.Dock = DockStyle.Fill;
            listStopped.View = View.Details;
            listStopped.FullRowSelect = true;
            listStopped.GridLines = true;
            listStopped.BackColor = KrotonTheme.BgDark;
            listStopped.ForeColor = KrotonTheme.TextPrimary;
            listStopped.Font = KrotonTheme.FontBody;
            listStopped.BorderStyle = BorderStyle.None;
            listStopped.Columns.Add("Serviço", 220);
            listStopped.Columns.Add("Nome exibido", 240);
            listStopped.Columns.Add("Descrição", 480);
            KrotonTheme.RoundCorners(listStopped, 8);

            tab.Controls.Add(listStopped);
            tab.Controls.Add(bar);
            return tab;
        }

        private TabPage BuildLogTab()
        {
            TabPage tab = new TabPage("Histórico de Logs");

            Panel bar = new Panel();
            bar.Dock = DockStyle.Top;
            bar.Height = 48;
            bar.BackColor = KrotonTheme.BgCard;

            btnRefreshLog = new RoundedButton();
            btnRefreshLog.Text = "Recarregar log";
            btnRefreshLog.Location = new Point(10, 10);
            btnRefreshLog.Width = 130;
            btnRefreshLog.Height = 27;
            KrotonTheme.StyleButton(btnRefreshLog, false);
            btnRefreshLog.Click += delegate { LoadLog(); };
            bar.Controls.Add(btnRefreshLog);

            txtLog = new TextBox();
            txtLog.Dock = DockStyle.Fill;
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.Font = new Font("Consolas", 9F);
            txtLog.WordWrap = false;
            txtLog.BackColor = KrotonTheme.BgDarkest;
            txtLog.ForeColor = KrotonTheme.TextPrimary;
            txtLog.BorderStyle = BorderStyle.None;
            KrotonTheme.RoundCorners(txtLog, 8);

            tab.Controls.Add(txtLog);
            tab.Controls.Add(bar);
            return tab;
        }

        // ---------------------------------------------------------------
        //  Logica de Ativacao / Desativacao e Protecao de Servicos
        // ---------------------------------------------------------------
        private void LoadServicesCatalog()
        {
            lblServiceStats.Text = "Lendo serviços...";
            btnReloadServices.Enabled = false;
            btnSearchOnline.Enabled = false;

            Thread t = new Thread(delegate() {
                try
                {
                    int exit;
                    string listDir = Path.Combine(ScriptRunner.BaseDir, "runtime");
                    if (!Directory.Exists(listDir))
                        Directory.CreateDirectory(listDir);
                    string jsonPath = Path.Combine(listDir, "services-catalog.json");

                    string scriptPath = Path.Combine(ScriptRunner.BaseDir, @"app\List-CatalogServices.ps1");
                    string cmd = string.Format("& '{0}' | Out-File -FilePath '{1}' -Encoding UTF8", scriptPath, jsonPath);
                    string diagOutput = ScriptRunner.RunPs(cmd, 35000, out exit);
                    if (exit != 0)
                        throw new InvalidOperationException(diagOutput);

                    string json = File.ReadAllText(jsonPath).Trim();
                    var items = new List<ServiceCatalogItem>();

                    if (json.Length > 0)
                    {
                        var ser = new JavaScriptSerializer();
                        var arr = ser.Deserialize<System.Collections.IList>(json);
                        if (arr != null)
                        {
                            foreach (object o in arr)
                            {
                                var d = o as Dictionary<string, object>;
                                if (d == null) continue;
                                items.Add(new ServiceCatalogItem {
                                    Name = Str(d, "Name"),
                                    DisplayName = Str(d, "DisplayName"),
                                    Description = Str(d, "Description"),
                                    Status = Str(d, "Status"),
                                    StartMode = Str(d, "StartMode"),
                                    Category = Str(d, "Category"),
                                    IsCritical = d.ContainsKey("IsCritical") && Convert.ToBoolean(d["IsCritical"]),
                                    Reason = Str(d, "Reason")
                                });
                            }
                        }
                    }

                    Invoke((MethodInvoker)delegate {
                        allServices = items;
                        FilterServices();
                        btnReloadServices.Enabled = true;
                        btnSearchOnline.Enabled = true;
                    });
                }
                catch (Exception ex)
                {
                    Invoke((MethodInvoker)delegate {
                        lblServiceStats.Text = "Erro ao listar";
                        btnReloadServices.Enabled = true;
                        btnSearchOnline.Enabled = true;
                        MessageBox.Show("Falha ao carregar serviços:\n" + ex.Message, "Serviços", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    });
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void FilterServices()
        {
            if (gridServices == null) return;
            string search = (txtSearchService.Text ?? "").Trim().ToLowerInvariant();
            string catFilter = cmbFilterCategory.SelectedItem != null ? cmbFilterCategory.SelectedItem.ToString() : "Todas";

            gridServices.Rows.Clear();
            int count = 0;
            int protectedCount = 0;

            foreach (var s in allServices)
            {
                if (catFilter != "Todas" && s.Category != catFilter) continue;

                if (search.Length > 0)
                {
                    bool match = (s.Name != null && s.Name.ToLowerInvariant().Contains(search))
                              || (s.DisplayName != null && s.DisplayName.ToLowerInvariant().Contains(search))
                              || (s.Description != null && s.Description.ToLowerInvariant().Contains(search));
                    if (!match) continue;
                }

                int idx = gridServices.Rows.Add();
                var row = gridServices.Rows[idx];
                row.Tag = s;

                row.Cells["colName"].Value = s.Name;
                string displayDesc = !string.IsNullOrEmpty(s.Description) ? s.Description : "(Sem descrição local)";
                row.Cells["colDesc"].Value = displayDesc;
                row.Cells["colCat"].Value = s.Category;
                row.Cells["colStatus"].Value = s.Status;

                if (s.IsCritical)
                {
                    row.Cells["colAction"].Value = "🔒 Protegido";
                    row.Cells["colBoot"].Value = "🔒 Protegido";
                    protectedCount++;
                }
                else
                {
                    bool isRunning = string.Equals(s.Status, "Running", StringComparison.OrdinalIgnoreCase);
                    row.Cells["colAction"].Value = isRunning ? "Parar" : "Iniciar";
                    row.Cells["colBoot"].Value = BootLabel(s.StartMode);
                }

                count++;
            }

            lblServiceStats.Text = string.Format("{0} exibidos ({1} protegidos)", count, protectedCount);
        }

        private void OnGridCellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            bool onAction = e.ColumnIndex == gridServices.Columns["colAction"].Index;
            bool onBoot = e.ColumnIndex == gridServices.Columns["colBoot"].Index;
            if (!onAction && !onBoot) return;

            var row = gridServices.Rows[e.RowIndex];
            var item = row.Tag as ServiceCatalogItem;
            if (item == null) return;

            if (onBoot)
            {
                ToggleServiceBoot(row, item);
                return;
            }

            if (item.IsCritical)
            {
                MessageBox.Show(
                    string.Format("O serviço '{0}' é um recurso crítico do Windows.\n\nMotivo da proteção:\n{1}\n\nPara garantir a estabilidade e segurança do sistema operacional, o Kroton.ia não permite desativá-lo.", item.Name, item.Reason),
                    "Serviço Protegido pelo Sistema",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            bool isRunning = string.Equals(item.Status, "Running", StringComparison.OrdinalIgnoreCase);
            string action = isRunning ? "Parar" : "Iniciar";
            string prompt = string.Format("Deseja {0} o serviço:\n\n{1} ({2})\n\nDescrição:\n{3}",
                action, item.DisplayName, item.Name, item.Description);

            if (MessageBox.Show(prompt, "Confirmação de Serviço", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            Cursor = Cursors.WaitCursor;
            try
            {
                int exit;
                string psCmd = isRunning
                    ? string.Format("Stop-Service -Name '{0}' -Force -ErrorAction SilentlyContinue", item.Name)
                    : string.Format("Start-Service -Name '{0}' -ErrorAction SilentlyContinue", item.Name);

                ScriptRunner.RunPs(psCmd, 15000, out exit);
                Thread.Sleep(800);

                // Re-consulta o estado real
                string checkCmd = string.Format("(Get-Service -Name '{0}').Status.ToString()", item.Name);
                string newStatus = ScriptRunner.RunPs(checkCmd, 5000, out exit).Trim();

                if (!string.IsNullOrEmpty(newStatus))
                {
                    item.Status = newStatus;
                    row.Cells["colStatus"].Value = newStatus;
                    bool nowRunning = string.Equals(newStatus, "Running", StringComparison.OrdinalIgnoreCase);
                    row.Cells["colAction"].Value = nowRunning ? "Parar" : "Iniciar";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Falha ao alterar serviço:\n" + ex.Message, "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void ToggleServiceBoot(DataGridViewRow row, ServiceCatalogItem item)
        {
            if (item.IsCritical)
            {
                MessageBox.Show(
                    string.Format("O serviço '{0}' é um recurso crítico do Windows.\n\nMotivo da proteção:\n{1}\n\nPara garantir a estabilidade e segurança do sistema operacional, o Kroton.ia não permite alterá-lo.", item.Name, item.Reason),
                    "Serviço Protegido pelo Sistema",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            bool isAuto = IsAutoStart(item.StartMode);
            string target = isAuto ? "Manual" : "Automatic";
            string targetLabel = isAuto ? "Manual" : "Automático";
            string prompt = string.Format("Deseja alterar a inicialização de\n\n{0} ({1})\n\npara: {2}\n\nDescrição:\n{3}",
                item.DisplayName, item.Name, targetLabel, item.Description);

            if (MessageBox.Show(prompt, "Confirmação de Inicialização", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            Cursor = Cursors.WaitCursor;
            try
            {
                int exit;
                string psCmd = string.Format("Set-Service -Name '{0}' -StartupType {1} -ErrorAction SilentlyContinue", item.Name, target);
                ScriptRunner.RunPs(psCmd, 15000, out exit);
                Thread.Sleep(500);

                string newMode = ScriptRunner.RunPs(
                    string.Format("(Get-Service -Name '{0}').StartType.ToString()", item.Name), 5000, out exit).Trim();
                if (string.IsNullOrEmpty(newMode)) return;

                item.StartMode = newMode;
                row.Cells["colBoot"].Value = BootLabel(newMode);
                gridServices.Refresh();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Falha ao alterar inicialização:\n" + ex.Message, "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private static bool IsAutoStart(string startMode)
        {
            return string.Equals(startMode, "Auto", StringComparison.OrdinalIgnoreCase)
                || string.Equals(startMode, "Automatic", StringComparison.OrdinalIgnoreCase)
                || string.Equals(startMode, "Boot", StringComparison.OrdinalIgnoreCase)
                || string.Equals(startMode, "System", StringComparison.OrdinalIgnoreCase);
        }

        private static string BootLabel(string startMode)
        {
            if (string.IsNullOrEmpty(startMode)) return "?";
            if (IsAutoStart(startMode)) return "Automático";
            if (string.Equals(startMode, "Manual", StringComparison.OrdinalIgnoreCase)) return "Manual";
            if (string.Equals(startMode, "Disabled", StringComparison.OrdinalIgnoreCase)) return "Desativado";
            return startMode;
        }

        private void OnGridCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= gridServices.Rows.Count) return;
            var row = gridServices.Rows[e.RowIndex];
            var item = row.Tag as ServiceCatalogItem;
            if (item == null) return;

            if (gridServices.Columns[e.ColumnIndex].Name == "colAction")
            {
                if (item.IsCritical)
                {
                    e.CellStyle.ForeColor = KrotonTheme.AccentLocked;
                    e.CellStyle.SelectionForeColor = KrotonTheme.AccentLocked;
                    e.CellStyle.Font = KrotonTheme.FontSmall;
                }
                else
                {
                    bool isRunning = string.Equals(item.Status, "Running", StringComparison.OrdinalIgnoreCase);
                    e.CellStyle.ForeColor = isRunning ? KrotonTheme.AccentDanger : KrotonTheme.AccentSuccess;
                    e.CellStyle.SelectionForeColor = Color.White;
                    e.CellStyle.Font = KrotonTheme.FontBodyBold;
                }
            }
            else if (gridServices.Columns[e.ColumnIndex].Name == "colBoot")
            {
                if (item.IsCritical)
                {
                    e.CellStyle.ForeColor = KrotonTheme.AccentLocked;
                    e.CellStyle.SelectionForeColor = KrotonTheme.AccentLocked;
                    e.CellStyle.Font = KrotonTheme.FontSmall;
                }
                else
                {
                    bool isDisabled = string.Equals(item.StartMode, "Disabled", StringComparison.OrdinalIgnoreCase);
                    bool isAuto = IsAutoStart(item.StartMode);
                    e.CellStyle.ForeColor = isDisabled ? KrotonTheme.AccentDanger : (isAuto ? KrotonTheme.GradientEnd : KrotonTheme.TextSecondary);
                    e.CellStyle.SelectionForeColor = Color.White;
                    e.CellStyle.Font = KrotonTheme.FontBodyBold;
                }
            }
            else if (gridServices.Columns[e.ColumnIndex].Name == "colStatus")
            {
                bool isRunning = string.Equals(item.Status, "Running", StringComparison.OrdinalIgnoreCase);
                e.CellStyle.ForeColor = isRunning ? KrotonTheme.AccentSuccess : KrotonTheme.TextMuted;
                e.CellStyle.Font = KrotonTheme.FontBodyBold;
            }
            else if (gridServices.Columns[e.ColumnIndex].Name == "colCat")
            {
                if (item.IsCritical)
                    e.CellStyle.ForeColor = KrotonTheme.AccentLocked;
                else if (item.Category == "Windows (Opcional)")
                    e.CellStyle.ForeColor = KrotonTheme.TextSecondary;
                else
                    e.CellStyle.ForeColor = KrotonTheme.GradientEnd;
            }
        }

        private void FetchMissingDescriptionsOnline()
        {
            var missing = allServices.FindAll(s => string.IsNullOrEmpty(s.Description) || s.Description == "(Sem descrição local)");
            if (missing.Count == 0)
            {
                MessageBox.Show("Todos os serviços listados já possuem descrição!", "Descrições", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            btnSearchOnline.Enabled = false;
            btnSearchOnline.Text = "Buscando na Web...";
            Cursor = Cursors.WaitCursor;

            Thread t = new Thread(delegate() {
                int foundCount = 0;
                foreach (var s in missing)
                {
                    string desc = ServiceDescriptionFetcher.FetchDescription(s.Name, s.DisplayName);
                    if (!string.IsNullOrEmpty(desc) && !desc.StartsWith("Descricao nao disponivel"))
                    {
                        s.Description = desc;
                        foundCount++;
                    }
                }

                Invoke((MethodInvoker)delegate {
                    Cursor = Cursors.Default;
                    btnSearchOnline.Enabled = true;
                    btnSearchOnline.Text = "Buscar descrições na Web";
                    FilterServices();
                    MessageBox.Show(
                        string.Format("Busca concluída!\n\n{0} descrições foram encontradas na Web e salvas em cache.", foundCount),
                        "Descrições Online",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // Descricao curta do modo selecionado, exibida abaixo do combo Comportamento
        private void UpdateModeHint()
        {
            string sel = cmbOperationMode.SelectedItem != null ? cmbOperationMode.SelectedItem.ToString() : "";
            if (sel.StartsWith("Modo Leve"))
                lblModeHint.Text = "Detecta o jogo e desativa só a telemetria";
            else if (sel.StartsWith("Desativado"))
                lblModeHint.Text = "Não altera serviços; apenas monitora";
            else
                lblModeHint.Text = "Detecta o jogo e aplica a otimização completa";
        }

        private void OnOperationModeChanged()
        {
            if (isUpdatingMode) return;
            UpdateModeHint();
            string selected = cmbOperationMode.SelectedItem != null ? cmbOperationMode.SelectedItem.ToString() : "";
            string modeKey = "game";

            if (selected.StartsWith("Modo Leve"))
                modeKey = "light";
            else if (selected.StartsWith("Desativado"))
                modeKey = "disabled";

            try
            {
                Dictionary<string, object> cfg = Program.LoadConfig();
                cfg["activeMode"] = modeKey;

                var ser = new JavaScriptSerializer();
                string json = ser.Serialize(cfg);
                string path = Path.Combine(ScriptRunner.BaseDir, "game-mode-config.json");
                File.WriteAllText(path, json, new UTF8Encoding(true));

                string descMsg = modeKey == "disabled"
                    ? "Otimização automática DESATIVADA. O runner não alterará nenhum serviço."
                    : (modeKey == "light" ? "Modo Leve ativado: desativa apenas telemetria ao detectar jogos." : "Modo Jogo ativado: otimização completa ao detectar jogos.");

                // Toast na bandeja confirmando a troca (a janela abaixo documenta o efeito)
                ShowModeToast("Comportamento alterado", descMsg);

                MessageBox.Show("Comportamento atualizado:\n\n" + descMsg, "Configuração", MessageBoxButtons.OK, MessageBoxIcon.Information);
                LoadStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erro ao salvar configuração:\n" + ex.Message, "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StartTimer()
        {
            statusTimer = new System.Windows.Forms.Timer();
            statusTimer.Interval = 5000;
            statusTimer.Tick += delegate { LoadStatus(); };
            statusTimer.Start();
        }

        private void LoadAll()
        {
            forceTaskCheck = true;
            LoadOperationModeConfig();
            LoadStatus();
            LoadServicesCatalog();
            LoadGames();
            LoadLog();
        }

        // ---------------------------------------------------------------
        //  Menu principal, bandeja (tray) e instalacao pela GUI
        // ---------------------------------------------------------------

        // Menu "Kroton.ia" no topo da janela: mostrar janela, instalar pelo
        // proprio executavel (sem passar pelo .bat) e saida real da aplicacao.
        private void BuildAppMenu()
        {
            mainMenu = new MenuStrip();
            mainMenu.BackColor = KrotonTheme.BgDarkest;
            mainMenu.ForeColor = KrotonTheme.TextPrimary;
            mainMenu.Font = KrotonTheme.FontSmall;
            mainMenu.Padding = new Padding(4, 2, 0, 2);

            ToolStripMenuItem mnuRoot = new ToolStripMenuItem("Kroton.ia");
            mnuRoot.ForeColor = KrotonTheme.TextPrimary;
            mnuRoot.DropDown.BackColor = KrotonTheme.BgCard;
            mnuRoot.DropDown.ForeColor = KrotonTheme.TextPrimary;

            ToolStripMenuItem mnuShow = new ToolStripMenuItem("Mostrar janela");
            mnuShow.ForeColor = KrotonTheme.TextPrimary;
            mnuShow.Click += delegate { RestoreFromTray(); };
            mnuRoot.DropDownItems.Add(mnuShow);

            mnuInstall = new ToolStripMenuItem("Instalar / Atualizar...");
            mnuInstall.ForeColor = KrotonTheme.TextPrimary;
            mnuInstall.Click += delegate { InstallFromGui(); };
            mnuRoot.DropDownItems.Add(mnuInstall);

            mnuRoot.DropDownItems.Add(new ToolStripSeparator());

            ToolStripMenuItem mnuExit = new ToolStripMenuItem("Sair");
            mnuExit.ForeColor = KrotonTheme.TextPrimary;
            mnuExit.Click += delegate { ExitApp(); };
            mnuRoot.DropDownItems.Add(mnuExit);

            mainMenu.Items.Add(mnuRoot);
        }

        // Icone na bandeja com o uso de RAM em tempo real (saber o que esta
        // sendo usado na memoria sem abrir a janela).
        private void InitTray()
        {
            trayMenu = new ContextMenuStrip();
            trayMenu.BackColor = KrotonTheme.BgCard;
            trayMenu.ForeColor = KrotonTheme.TextPrimary;
            trayMenu.Font = KrotonTheme.FontBody;

            ToolStripMenuItem tsmShow = new ToolStripMenuItem("Abrir Kroton.ia");
            tsmShow.ForeColor = KrotonTheme.TextPrimary;
            tsmShow.Click += delegate { RestoreFromTray(); };
            trayMenu.Items.Add(tsmShow);

            ToolStripMenuItem tsmMem = new ToolStripMenuItem("Ver memória por aplicativo");
            tsmMem.ForeColor = KrotonTheme.TextPrimary;
            tsmMem.Click += delegate
            {
                RestoreFromTray();
                if (tabs != null && memoryTab != null) tabs.SelectedTab = memoryTab;
            };
            trayMenu.Items.Add(tsmMem);

            trayMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem tsmExit = new ToolStripMenuItem("Sair");
            tsmExit.ForeColor = KrotonTheme.TextPrimary;
            tsmExit.Click += delegate { ExitApp(); };
            trayMenu.Items.Add(tsmExit);

            trayIcon = new NotifyIcon();
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.Icon = DrawTrayIcon(-1);
            trayIcon.Text = "Kroton.ia";
            trayIcon.Visible = true;
            trayIcon.DoubleClick += delegate { RestoreFromTray(); };

            trayTimer = new System.Windows.Forms.Timer();
            trayTimer.Interval = 5000;
            trayTimer.Tick += delegate { UpdateTray(); };
            trayTimer.Start();
            UpdateTray();
        }

        // Atualiza icone (% de RAM com cor por faixa) e tooltip da bandeja.
        private void UpdateTray()
        {
            if (trayIcon == null || IsDisposed) return;

            uint load;
            ulong total, avail;
            int pct = -1;
            if (MemoryPanel.TryGetUsage(out load, out total, out avail))
            {
                pct = (int)load;
                double freeGb = avail / 1073741824.0;

                string modeLabel = lastModeKey == "game" ? "JOGO"
                    : lastModeKey == "light" ? "LEVE"
                    : lastModeKey == "disabled" ? "DESLIGADO" : "NORMAL";
                string txt = "RAM " + pct + "% | " + freeGb.ToString("0.0") + " GB livres | " + modeLabel;
                if (txt.Length > 63) txt = txt.Substring(0, 63);
                try { trayIcon.Text = txt; } catch { }
            }

            Icon oldIcon = trayIcon.Icon;
            Icon newIcon = DrawTrayIcon(pct);
            if (newIcon != null)
            {
                trayIcon.Icon = newIcon;
                if (oldIcon != null && !ReferenceEquals(oldIcon, newIcon))
                {
                    try { oldIcon.Dispose(); } catch { }
                }
            }
        }

        // Desenha o icone da bandeja: circulo com o % de RAM (verde < 60%,
        // amarelo 60-79%, vermelho >= 80%). percent < 0 = icone do app.
        private static Icon DrawTrayIcon(int percent)
        {
            try
            {
                if (percent < 0)
                {
                    Icon baseIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                    if (baseIcon != null) return baseIcon;
                }

                using (Bitmap bmp = new Bitmap(16, 16))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.Clear(Color.Transparent);

                        Color bg = percent >= 80 ? KrotonTheme.AccentDanger
                            : percent >= 60 ? KrotonTheme.AccentWarning
                            : KrotonTheme.GradientEnd;
                        using (Brush b = new SolidBrush(Color.FromArgb(235, bg)))
                        {
                            g.FillEllipse(b, 0, 0, 15, 15);
                        }

                        string txt = percent >= 100 ? "99+" : Math.Max(0, percent).ToString();
                        float fontSize = txt.Length >= 3 ? 6f : 7.5f;
                        using (Font f = new Font("Segoe UI", fontSize, FontStyle.Bold))
                        using (Brush tb = new SolidBrush(Color.FromArgb(14, 22, 38)))
                        {
                            SizeF sz = g.MeasureString(txt, f);
                            g.DrawString(txt, f, tb, (16 - sz.Width) / 2f, (16 - sz.Height) / 2f);
                        }
                    }

                    IntPtr h = bmp.GetHicon();
                    try
                    {
                        using (Icon tmp = Icon.FromHandle(h)) return (Icon)tmp.Clone();
                    }
                    finally
                    {
                        DestroyIcon(h);
                    }
                }
            }
            catch { }
            return null;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        // Esconde na bandeja (X ou minimizar); saida real pelo menu Sair.
        private void HideToTray()
        {
            if (Visible) Hide();
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;

            if (!trayBalloonShown && trayIcon != null)
            {
                trayBalloonShown = true;
                trayIcon.ShowBalloonTip(4000, "Kroton.ia",
                    "A interface foi para a bandeja e o monitor continua ativo.\n" +
                    "Duplo clique no icone para abrir; Sair (menu) encerra o app.",
                    ToolTipIcon.Info);
            }
        }

        private void RestoreFromTray()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            BringToFront();
            Activate();
            forceTaskCheck = true;
            LoadStatus();
        }

        private void ExitApp()
        {
            reallyExit = true;
            Close();
        }

        // Pausa a amostragem da aba Memoria (2s + enumeracao de processos) quando
        // a janela esta oculta ou a aba nao e' a ativa - alivia o consumo geral.
        private void SetSamplingState()
        {
            if (memoryPanel == null || tabs == null) return;
            bool want = Visible && tabs.SelectedTab == memoryTab;
            memoryPanel.SetSampling(want);
        }

        /// <summary>
        /// Instala/atualiza o Kroton.ia pelo proprio executavel (sem o .bat):
        /// roda install\Install-Kroton.ps1 (ou Install-Kroton.ps1 na raiz, no
        /// layout plano), elevando via UAC se a GUI nao for administrador.
        /// </summary>
        private void InstallFromGui()
        {
            string installer = Path.Combine(ScriptRunner.BaseDir, @"install\Install-Kroton.ps1");
            if (!File.Exists(installer))
                installer = Path.Combine(ScriptRunner.BaseDir, "Install-Kroton.ps1");
            if (!File.Exists(installer))
            {
                MessageBox.Show(
                    "Instalador não encontrado:\n" + Path.Combine(ScriptRunner.BaseDir, @"install\Install-Kroton.ps1"),
                    "Instalar / Atualizar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string installPath = Path.Combine(programFiles, "KrotonIA");
            try
            {
                object registeredPath = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Uninstall\KrotonIA",
                    "InstallLocation",
                    null);
                if (registeredPath != null && Directory.Exists(Convert.ToString(registeredPath)))
                    installPath = Convert.ToString(registeredPath);
            }
            catch { }

            using (FolderBrowserDialog folderDialog = new FolderBrowserDialog())
            {
                folderDialog.Description =
                    "Selecione a pasta exata de instalacao. Selecione Program Files para usar o padrao KrotonIA, ou escolha/crie outra pasta.";
                folderDialog.ShowNewFolderButton = true;
                folderDialog.SelectedPath = Directory.Exists(installPath) ? installPath : programFiles;
                if (folderDialog.ShowDialog(this) != DialogResult.OK) return;

                string selectedPath = Path.GetFullPath(folderDialog.SelectedPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string programFilesPath = Path.GetFullPath(programFiles)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                installPath = string.Equals(selectedPath, programFilesPath, StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(programFilesPath, "KrotonIA")
                    : selectedPath;
            }

            string prompt =
                "Instalar / atualizar o Kroton.ia em:\n" + installPath + "\n\n" +
                "O instalador copia os arquivos, (re)registra a tarefa agendada,\n" +
                "cria o atalho na Área de Trabalho e registra no Painel de Controle.\n\n" +
                "Requer permissão de administrador (UAC).\n" +
                "Atenção: a tarefa agendada passará a rodar de:\n" + installPath + "\n\n" +
                "Continuar?";
            if (MessageBox.Show(prompt, "Instalar / Atualizar",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            mnuInstall.Enabled = false;
            mnuInstall.Text = "Instalando...";
            RunInstallerWorker(installer, installPath);
        }

        // Thread do instalador: executa o .ps1 (captura stdout se ja for admin,
        // senao eleva via UAC gravando a saida em runtime\install.log).
        private void RunInstallerWorker(string installer, string installPath)
        {
            Thread t = new Thread(delegate()
            {
                string error = null;
                int exit = -1;
                string tail = "";
                try
                {
                    string logPath = Path.Combine(ScriptRunner.BaseDir, @"runtime\install.log");
                    string escapedInstaller = installer.Replace("'", "''");
                    string escapedInstallPath = installPath.Replace("'", "''");
                    if (ScriptRunner.IsAdmin())
                    {
                        string command = "& '" + escapedInstaller + "' -InstallPath '" +
                            escapedInstallPath + "'";
                        string output = ScriptRunner.RunPs(command, 240000, out exit);
                        tail = ScriptRunner.Tail(output, 12);
                    }
                    else
                    {
                        try { Directory.CreateDirectory(Path.GetDirectoryName(logPath)); } catch { }
                        if (File.Exists(logPath)) { try { File.Delete(logPath); } catch { } }

                        System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo();
                        psi.FileName = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.System),
                            @"WindowsPowerShell\v1.0\powershell.exe");
                        string escapedLogPath = logPath.Replace("'", "''");
                        string command = "& '" + escapedInstaller + "' -InstallPath '" +
                            escapedInstallPath + "' *> '" + escapedLogPath + "'";
                        psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" + command + "\"";
                        psi.UseShellExecute = true;
                        psi.Verb = "runas";

                        using (System.Diagnostics.Process proc = System.Diagnostics.Process.Start(psi))
                        {
                            if (proc == null) throw new InvalidOperationException("Não foi possível iniciar o instalador.");
                            if (!proc.WaitForExit(240000))
                            {
                                try { proc.Kill(); } catch { }
                                throw new TimeoutException("O instalador excedeu o tempo limite (4 min).");
                            }
                            exit = proc.ExitCode;
                        }
                        tail = File.Exists(logPath)
                            ? ScriptRunner.Tail(File.ReadAllText(logPath), 12)
                            : "(sem saída de log)";
                    }
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    error = "Instalação cancelada: permissão de administrador não concedida (UAC).";
                }
                catch (Exception ex)
                {
                    error = "Erro ao instalar:\n" + ex.Message;
                }

                if (IsDisposed) return;
                try
                {
                    Invoke((MethodInvoker)delegate { FinishInstall(exit, error, tail, installPath); });
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        // Retorno da UI apos o instalador terminar (sempre na thread principal).
        private void FinishInstall(int exit, string error, string tail, string installPath)
        {
            mnuInstall.Enabled = true;
            mnuInstall.Text = "Instalar / Atualizar...";

            if (error != null)
            {
                MessageBox.Show(error, "Instalar / Atualizar", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (exit != 0)
            {
                MessageBox.Show("Falha na instalação (código " + exit + "):\n" + tail,
                    "Instalar / Atualizar", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string installedExe = Path.Combine(installPath, "KrotonIA.exe");
            string baseDirFull = Path.GetFullPath(ScriptRunner.BaseDir).TrimEnd('\\');
            string installFull = Path.GetFullPath(installPath).TrimEnd('\\');
            bool sameBase = string.Equals(baseDirFull, installFull, StringComparison.OrdinalIgnoreCase);

            string msg = "Instalação concluída!\n\n" + tail;
            if (!sameBase && File.Exists(installedExe))
            {
                // Contrato BaseDir unico: GUI e runner precisam da mesma raiz.
                if (MessageBox.Show(
                    msg + "\n\nInterface aberta de:\n" + ScriptRunner.BaseDir +
                    "\nInstalação em:\n" + installPath +
                    "\n\nPara status, log e Comportamento corretos, a interface deve " +
                    "rodar da mesma pasta do monitor.\n\nReabrir a interface instalada agora?",
                    "Instalar / Atualizar", MessageBoxButtons.YesNo, MessageBoxIcon.Information)
                    == DialogResult.Yes)
                {
                    System.Diagnostics.Process.Start(installedExe);
                    reallyExit = true;
                    Close();
                    return;
                }
            }
            else
            {
                MessageBox.Show(msg, "Instalar / Atualizar", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            forceTaskCheck = true;
            LoadStatus();
            LoadLog();
        }

        private void LoadOperationModeConfig()
        {
            try
            {
                Dictionary<string, object> cfg = Program.LoadConfig();
                string activeMode = "game";
                if (cfg.ContainsKey("activeMode") && cfg["activeMode"] != null)
                {
                    activeMode = cfg["activeMode"].ToString();
                }

                isUpdatingMode = true;
                if (activeMode == "disabled")
                    cmbOperationMode.SelectedIndex = 2;
                else if (activeMode == "light")
                    cmbOperationMode.SelectedIndex = 1;
                else
                    cmbOperationMode.SelectedIndex = 0;
            }
            catch
            {
                cmbOperationMode.SelectedIndex = 0;
            }
            finally
            {
                isUpdatingMode = false;
                UpdateModeHint();
            }
        }

        // Aba "Modo Jogo": modo atual, desde quando e a lista de servicos
        // mantidos parados (servicesStoppedNames do game-mode-state.json).
        private void UpdateGameModeTab(string mode, int stopped, string changedAt, List<string> names)
        {
            if (gameModeTab == null || listStopped == null || lblGameStatus == null) return;

            bool game = (mode == "game" || mode == "light");
            if (game)
            {
                lblGameStatus.Text = "MODO JOGO: ATIVO";
                lblGameStatus.ForeColor = KrotonTheme.AccentWarning;
                string detail = stopped > 0
                    ? stopped + " serviço(s) mantido(s) parado(s) para o jogo"
                    : "Serviços otimizados para o jogo";
                if (changedAt.Length >= 16)
                    detail += "  |  desde " + changedAt.Substring(11, 5);
                detail += "  |  restauração automática ao fechar o jogo";
                if (stopped > 0 && names.Count == 0)
                    detail += "  (lista registrada a partir da próxima ativação)";
                lblGameDetail.Text = detail;
            }
            else
            {
                lblGameStatus.Text = "MODO JOGO: INATIVO";
                lblGameStatus.ForeColor = KrotonTheme.AccentSuccess;
                lblGameDetail.Text =
                    "Nenhum serviço mantido parado. A lista aparece aqui quando um jogo detectado ativa o modo.";
                names = new List<string>();
            }

            listStopped.BeginUpdate();
            try
            {
                listStopped.Items.Clear();
                foreach (string svcName in names)
                {
                    ListViewItem item = new ListViewItem(svcName);
                    string display = svcName;
                    string desc = "";
                    foreach (ServiceCatalogItem c in allServices)
                    {
                        if (string.Equals(c.Name, svcName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrEmpty(c.DisplayName)) display = c.DisplayName;
                            if (!string.IsNullOrEmpty(c.Description)) desc = c.Description;
                            break;
                        }
                    }
                    item.SubItems.Add(display);
                    item.SubItems.Add(desc);
                    listStopped.Items.Add(item);
                }
            }
            finally
            {
                listStopped.EndUpdate();
            }
        }

        private string StatePath
        {
            get { return Path.Combine(ScriptRunner.BaseDir, @"runtime\game-mode-state.json"); }
        }

        private string LogPath
        {
            get { return Path.Combine(ScriptRunner.BaseDir, @"runtime\game-mode.log"); }
        }

        // Notificacao na bandeja (toast) - usada ao alternar modo/comportamento.
        private void ShowModeToast(string title, string body)
        {
            if (trayIcon == null || !trayIcon.Visible) return;
            try { trayIcon.ShowBalloonTip(3000, title, body, ToolTipIcon.Info); }
            catch { }
        }

        private void LoadStatus()
        {
            // Valor do modo na carga ANTERIOR - usado para o toast de alternancia
            // (na primeira carga e "" e nenhum toast e disparado).
            string prevModeKey = lastModeKey;
            string mode = "normal";
            int stopped = 0;
            string changedAt = "";
            List<string> stoppedNames = new List<string>();
            try
            {
                if (File.Exists(StatePath))
                {
                    string json = File.ReadAllText(StatePath);
                    Match m = Regex.Match(json, "\"mode\"\\s*:\\s*\"([^\"]+)\"");
                    if (m.Success) mode = m.Groups[1].Value;
                    Match m2 = Regex.Match(json, "\"servicesStopped\"\\s*:\\s*(\\d+)");
                    if (m2.Success) int.TryParse(m2.Groups[1].Value, out stopped);
                    Match m3 = Regex.Match(json, "\"changedAt\"\\s*:\\s*\"([^\"]+)\"");
                    if (m3.Success) changedAt = m3.Groups[1].Value;
                    Match m4 = Regex.Match(json, "\"servicesStoppedNames\"\\s*:\\s*\\[([^\\]]*)\\]");
                    if (m4.Success)
                    {
                        foreach (Match nm in Regex.Matches(m4.Groups[1].Value, "\"([^\"]+)\""))
                        {
                            stoppedNames.Add(nm.Groups[1].Value);
                        }
                    }
                }
            }
            catch { }

            // Aba "Modo Jogo": modo atual + lista de servicos parados
            UpdateGameModeTab(mode, stopped, changedAt, stoppedNames);

            string activeModeKey = "game";
            try
            {
                var cfg = Program.LoadConfig();
                if (cfg.ContainsKey("activeMode") && cfg["activeMode"] != null)
                    activeModeKey = cfg["activeMode"].ToString();
            }
            catch { }

            if (activeModeKey == "disabled")
            {
                lastModeKey = "disabled";
                lblMode.Text = "MODO: DESATIVADO (apenas monitoramento)";
                lblMode.ForeColor = KrotonTheme.TextMuted;
            }
            else if (mode == "game" || mode == "light")
            {
                lastModeKey = mode;
                // Informa quantos servicos foram parados na entrada do modo jogo
                lblMode.Text = stopped > 0
                    ? "MODO: JOGO ATIVO (" + stopped + " serviços parados)"
                    : "MODO: JOGO ATIVO (serviços otimizados)";
                lblMode.ForeColor = KrotonTheme.AccentWarning;
            }
            else
            {
                lastModeKey = "normal";
                lblMode.Text = "MODO: NORMAL (sistema equilibrado)";
                lblMode.ForeColor = KrotonTheme.AccentSuccess;
            }

            // Toast quando o runner alterna normal <-> jogo (transicoes reais).
            // "disabled" nao notifica aqui: e' acao do usuario no combo, que ja
            // mostra o proprio toast ao trocar o Comportamento.
            if (prevModeKey.Length > 0 && prevModeKey != lastModeKey)
            {
                bool toGame = (lastModeKey == "game" || lastModeKey == "light") && prevModeKey == "normal";
                bool toNormal = lastModeKey == "normal" && (prevModeKey == "game" || prevModeKey == "light");
                if (toGame)
                {
                    ShowModeToast("Modo JOGO ativado",
                        stopped > 0
                            ? stopped + " serviço(s) mantido(s) parado(s) para o jogo."
                            : "Serviços otimizados para o jogo.");
                }
                else if (toNormal)
                {
                    ShowModeToast("Modo NORMAL restaurado",
                        "Serviços de volta ao estado original - monitor continua ativo.");
                }
            }

            // A checagem da tarefa spawna powershell.exe (custo real de CPU): roda
            // na primeira carga, quando forcada (botao Atualizar) e depois a cada
            // 6o tick. Arquivos de estado/config sao lidos sempre (baratos).
            statusTick++;
            bool checkTask = forceTaskCheck || (statusTick % 6) == 0;
            forceTaskCheck = false;
            if (!checkTask) return;

            try
            {
                int exit;
                string cmd = "$t = Get-ScheduledTask -TaskName 'KrotonIA-GameModeRunner' -ErrorAction SilentlyContinue; " +
                             "if (-not $t) { 'NAO_INSTALADA' } " +
                             "elseif ($t.State -eq 'Disabled') { 'DESABILITADA' } " +
                             "else { 'ATIVA:' + $t.State }";
                string output = ScriptRunner.RunPs(cmd, 8000, out exit).Trim();
                if (output.StartsWith("NAO"))
                    lblMonitor.Text = "Monitor: tarefa não instalada (menu Kroton.ia > Instalar)";
                else if (output.StartsWith("DESABILITADA"))
                    lblMonitor.Text = "Monitor: DESABILITADO no Agendador de Tarefas";
                else if (output.StartsWith("ATIVA"))
                    lblMonitor.Text = "Monitor: ATIVO (" + output.Substring(5).Trim() + ")";
                else
                    lblMonitor.Text = "Monitor: " + output;
            }
            catch
            {
                lblMonitor.Text = "Monitor: não foi possível verificar";
            }
        }

        // ---------------------------------------------------------------
        //  Aba Otimizacao (Auditoria e perfis em lote)
        // ---------------------------------------------------------------
        private void RunAudit()
        {
            btnAudit.Enabled = false;
            txtAuditSummary.Text = "Auditando...";
            listAudit.Items.Clear();
            Application.DoEvents();

            try
            {
                int exit;
                string cmd = string.Format(
                    "& '{0}' -Mode Audit -ProfilePath '{1}'",
                    Path.Combine(ScriptRunner.BaseDir, "Optimize-BackgroundServices.ps1"),
                    Path.Combine(ScriptRunner.BaseDir, "service-profile.json"));
                string output = ScriptRunner.RunPs(cmd, 60000, out exit);

                string json = ScriptRunner.ExtractJson(output);
                if (exit != 0 || json.Length == 0)
                {
                    txtAuditSummary.Text = "Falha na auditoria";
                    MessageBox.Show("Saída:\n" + ScriptRunner.Tail(output, 8),
                        "Auditoria", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> data = ser.DeserializeObject(json) as Dictionary<string, object>;
                if (data == null)
                {
                    txtAuditSummary.Text = "JSON inválido";
                    return;
                }

                object recsObj;
                data.TryGetValue("Recommendations", out recsObj);
                Array recs = recsObj as Array;
                if (recs == null && recsObj is Dictionary<string, object>)
                {
                    recs = new object[] { recsObj };
                }

                object actObj;
                data.TryGetValue("ActiveServiceCount", out actObj);

                if (recs != null)
                {
                    foreach (object item in recs)
                    {
                        Dictionary<string, object> r = item as Dictionary<string, object>;
                        if (r == null) continue;
                        ListViewItem li = new ListViewItem(Str(r, "Name"));
                        li.SubItems.Add(Str(r, "DisplayName"));
                        li.SubItems.Add(Str(r, "CurrentStart"));
                        li.SubItems.Add(Str(r, "ProposedStart"));
                        li.SubItems.Add(Str(r, "Confidence"));
                        listAudit.Items.Add(li);
                    }
                }

                int active = actObj is int ? (int)actObj : 0;
                int cand = recs != null ? recs.Length : 0;
                txtAuditSummary.Text = string.Format("{0} ativos | {1} candidatos a otimização", active, cand);
            }
            catch (Exception ex)
            {
                txtAuditSummary.Text = "Erro";
                MessageBox.Show(ex.Message, "Auditoria", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnAudit.Enabled = true;
            }
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null) return v.ToString();
            return "";
        }

        private void ApplyMode(string target)
        {
            string label = target == "game" ? "MODO JOGO" : "RESTAURAÇÃO";
            string question = target == "game"
                ? "Ativar o Modo Jogo agora?\n\nIsso desativará serviços desnecessários em segundo plano para liberar CPU e memória."
                : "Restaurar o estado original de todos os serviços?";
            if (MessageBox.Show(question, label, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            bool targetGame = target == "game";
            btnApplyGame.Enabled = false;
            btnRollback.Enabled = false;
            Cursor = Cursors.WaitCursor;
            Application.DoEvents();

            try
            {
                int exit;
                string cmd;
                if (targetGame)
                {
                    cmd = string.Format(
                        "& '{0}' -Mode Apply -ProfilePath '{1}' -BackupPath '{2}'",
                        Path.Combine(ScriptRunner.BaseDir, "Optimize-BackgroundServices.ps1"),
                        Path.Combine(ScriptRunner.BaseDir, "service-profile.game.json"),
                        Path.Combine(ScriptRunner.BaseDir, @"backup\services-backup-game.json"));
                }
                else
                {
                    string backup = Path.Combine(ScriptRunner.BaseDir, @"backup\services-backup-game.json");
                    if (File.Exists(backup))
                    {
                        cmd = string.Format("& '{0}' -Mode Rollback -BackupPath '{1}'",
                            Path.Combine(ScriptRunner.BaseDir, "Optimize-BackgroundServices.ps1"), backup);
                    }
                    else
                    {
                        cmd = string.Format(
                            "& '{0}' -Mode Apply -ProfilePath '{1}' -BackupPath '{2}'",
                            Path.Combine(ScriptRunner.BaseDir, "Optimize-BackgroundServices.ps1"),
                            Path.Combine(ScriptRunner.BaseDir, "service-profile.normal.json"),
                            Path.Combine(ScriptRunner.BaseDir, @"backup\services-backup-normal.json"));
                    }
                }

                string output = ScriptRunner.RunPs(cmd, 120000, out exit);

                // Resumo do Optimize: quantos servicos o Apply parou de verdade
                int stoppedServices = 0;
                Match sumMatch = Regex.Match(output ?? "", @"\[SUMMARY\]\s+stopped=(\d+)");
                if (sumMatch.Success) int.TryParse(sumMatch.Groups[1].Value, out stoppedServices);

                if (exit == 0)
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
                        string mode = targetGame ? "game" : "normal";
                        File.WriteAllText(StatePath,
                            "{ \"mode\": \"" + mode + "\", \"changedAt\": \"" + DateTime.Now.ToString("s") +
                            "\", \"servicesStopped\": " + stoppedServices + " }",
                            new UTF8Encoding(false));
                    }
                    catch { }

                    string detail = targetGame && stoppedServices > 0
                        ? "Serviços parados para o jogo: " + stoppedServices + "\n\n"
                        : "";
                    MessageBox.Show(detail + "Concluído com sucesso!\n\n" + ScriptRunner.Tail(output, 15),
                        label, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show("Falha (código " + exit + "):\n" + ScriptRunner.Tail(output, 15),
                        label, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, label, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnApplyGame.Enabled = true;
                btnRollback.Enabled = true;
                Cursor = Cursors.Default;
                LoadStatus();
                LoadLog();
                LoadServicesCatalog();
            }
        }

        // ---------------------------------------------------------------
        //  Aba Jogos e Aba Log
        // ---------------------------------------------------------------
        private void LoadGames()
        {
            games.Clear();
            listGames.Items.Clear();
            try
            {
                Dictionary<string, object> cfg = Program.LoadConfig();
                object names;
                if (cfg.TryGetValue("gameProcessNames", out names) && names is Array)
                {
                    foreach (object n in (Array)names)
                    {
                        string s = n == null ? "" : n.ToString();
                        if (s.Length > 0) games.Add(s);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erro ao ler game-mode-config.json:\n" + ex.Message,
                    "Jogos", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            foreach (string g in games) listGames.Items.Add(g);
        }

        private void AddGame()
        {
            string name = txtNewGame.Text.Trim();
            if (name.Length == 0)
            {
                MessageBox.Show("Digite o nome do executável (ex: meujogo.exe).",
                    "Jogos", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - 4);
            }
            foreach (string g in games)
            {
                if (string.Equals(g, name, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("Este jogo já está na lista.", "Jogos",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }
            games.Add(name);
            listGames.Items.Add(name);
            txtNewGame.Clear();
        }

        private void RemoveGame()
        {
            if (listGames.SelectedIndex < 0) return;
            games.RemoveAt(listGames.SelectedIndex);
            listGames.Items.RemoveAt(listGames.SelectedIndex);
        }

        private void SaveGames()
        {
            try
            {
                Dictionary<string, object> cfg = Program.LoadConfig();
                cfg["gameProcessNames"] = new ArrayList(games);

                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                string json = ser.Serialize(cfg);

                string path = Path.Combine(ScriptRunner.BaseDir, "game-mode-config.json");
                File.WriteAllText(path, json, new UTF8Encoding(true));
                MessageBox.Show("Lista de jogos salva com sucesso! Entrará em vigor na próxima verificação (máx 20s).",
                    "Jogos", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erro ao salvar:\n" + ex.Message,
                    "Jogos", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ---------------------------------------------------------------
        //  Exportar / Importar lista de jogos
        // ---------------------------------------------------------------

        /// <summary>
        /// Exporta a lista monitorada para um JSON proprio (formato kroton-games):
        /// util para backup da lista ou levar para outra maquina.
        /// </summary>
        private void ExportGames()
        {
            if (games.Count == 0)
            {
                MessageBox.Show("A lista de jogos está vazia.", "Exportar jogos",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = "Exportar lista de jogos";
                dlg.Filter = "JSON (*.json)|*.json|Todos os arquivos (*.*)|*.*";
                dlg.FileName = "kroton-jogos-" + DateTime.Now.ToString("yyyy-MM-dd") + ".json";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    Dictionary<string, object> payload = new Dictionary<string, object>();
                    payload["format"] = "kroton-games";
                    payload["version"] = 1;
                    payload["exportedAt"] = DateTime.Now.ToString("s");
                    payload["games"] = new ArrayList(games);

                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    ser.MaxJsonLength = int.MaxValue;
                    File.WriteAllText(dlg.FileName, ser.Serialize(payload), new UTF8Encoding(false));

                    MessageBox.Show(string.Format("{0} jogo(s) exportado(s) para:\n{1}",
                        games.Count, dlg.FileName), "Exportar jogos",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Erro ao exportar:\n" + ex.Message, "Exportar jogos",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        /// <summary>
        /// Importa uma lista exportada (objeto {"games":[...]} ou um array puro de
        /// nomes), mesclando com a atual - HashSet cobre lista + lote, padrao do
        /// DetectGames (evita duplicatas de qualquer fonte).
        /// </summary>
        private void ImportGames()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Importar lista de jogos";
                dlg.Filter = "JSON (*.json)|*.json|Todos os arquivos (*.*)|*.*";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    object root = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(dlg.FileName));
                    Array arr = null;
                    Dictionary<string, object> asDict = root as Dictionary<string, object>;
                    if (asDict != null)
                    {
                        object g;
                        if (asDict.TryGetValue("games", out g)) arr = g as Array;
                    }
                    if (arr == null) arr = root as Array;
                    if (arr == null)
                    {
                        MessageBox.Show(
                            "Arquivo inválido: esperado {\"games\": [...]} ou um array de nomes de processo.",
                            "Importar jogos", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    int added = 0, existing = 0;
                    HashSet<string> known = new HashSet<string>(games, StringComparer.OrdinalIgnoreCase);
                    foreach (object o in arr)
                    {
                        string name = o == null ? "" : o.ToString().Trim();
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            name = name.Substring(0, name.Length - 4).Trim();
                        if (name.Length == 0) continue;
                        if (!known.Add(name)) { existing++; continue; }
                        games.Add(name);
                        listGames.Items.Add(name);
                        added++;
                    }

                    string msg = string.Format("Importados: {0} novo(s)\nJá existentes: {1}", added, existing);
                    if (added > 0)
                    {
                        if (MessageBox.Show(msg + "\n\nSalvar a lista agora?", "Importar jogos",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                        {
                            SaveGames();
                        }
                    }
                    else
                    {
                        MessageBox.Show(msg, "Importar jogos", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Erro ao importar:\n" + ex.Message, "Importar jogos",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // ---------------------------------------------------------------
        //  Perfil por jogo (perGameProfiles no game-mode-config.json)
        // ---------------------------------------------------------------

        // Monta o menu de perfil do item selecionado: estado atual + acoes
        // (criar copia do padrao, escolher arquivo, editar, remover).
        private void BuildGameProfileMenu()
        {
            gameMenu.Items.Clear();
            string game = listGames.SelectedItem != null ? listGames.SelectedItem.ToString() : "";
            if (game.Length == 0) return;

            string profilePath;
            bool custom = TryGetPerGameProfile(game, out profilePath);

            ToolStripMenuItem status = new ToolStripMenuItem(custom
                ? "Perfil personalizado: " + profilePath
                : "Perfil: padrão (service-profile.game.json)");
            status.Enabled = false;
            gameMenu.Items.Add(status);
            gameMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem mnuCreate = new ToolStripMenuItem("Criar perfil personalizado (cópia do padrão)");
            mnuCreate.Enabled = !custom;
            mnuCreate.Click += delegate { CreateGameProfile(game); };
            gameMenu.Items.Add(mnuCreate);

            ToolStripMenuItem mnuPick = new ToolStripMenuItem(custom
                ? "Trocar arquivo de perfil..." : "Escolher arquivo de perfil...");
            mnuPick.Click += delegate { PickGameProfile(game); };
            gameMenu.Items.Add(mnuPick);

            ToolStripMenuItem mnuEdit = new ToolStripMenuItem("Editar perfil (abrir arquivo)");
            mnuEdit.Enabled = custom;
            mnuEdit.Click += delegate { EditGameProfile(profilePath); };
            gameMenu.Items.Add(mnuEdit);

            ToolStripMenuItem mnuRemove = new ToolStripMenuItem("Remover personalização (voltar ao padrão)");
            mnuRemove.Enabled = custom;
            mnuRemove.Click += delegate { RemoveGameProfile(game); };
            gameMenu.Items.Add(mnuRemove);
        }

        /// <summary>Le perGameProfiles do config (chave ausente = dict vazio).</summary>
        private Dictionary<string, object> LoadPerGameProfiles()
        {
            try
            {
                Dictionary<string, object> cfg = Program.LoadConfig();
                object v;
                if (cfg.TryGetValue("perGameProfiles", out v) && v is Dictionary<string, object>)
                    return (Dictionary<string, object>)v;
            }
            catch { }
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Retorna o profilePath do jogo (relativo ao BaseDir) se houver.</summary>
        private bool TryGetPerGameProfile(string game, out string profilePath)
        {
            profilePath = "";
            foreach (KeyValuePair<string, object> kv in LoadPerGameProfiles())
            {
                if (!string.Equals(kv.Key, game, StringComparison.OrdinalIgnoreCase)) continue;
                Dictionary<string, object> entry = kv.Value as Dictionary<string, object>;
                if (entry != null)
                {
                    object pp;
                    if (entry.TryGetValue("profilePath", out pp) && pp != null)
                        profilePath = pp.ToString();
                }
                break;
            }
            return profilePath.Length > 0;
        }

        // Grava (ou remove, com path vazio) o perfil do jogo preservando as
        // demais chaves do config - padrao SaveGames/OnOperationModeChanged.
        private void SavePerGameProfile(string game, string relativeProfilePath)
        {
            Dictionary<string, object> cfg = Program.LoadConfig();

            object v;
            Dictionary<string, object> perGame;
            if (cfg.TryGetValue("perGameProfiles", out v) && v is Dictionary<string, object>)
                perGame = (Dictionary<string, object>)v;
            else
                perGame = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            string existingKey = null;
            foreach (string k in perGame.Keys)
            {
                if (string.Equals(k, game, StringComparison.OrdinalIgnoreCase)) { existingKey = k; break; }
            }
            if (existingKey != null) perGame.Remove(existingKey);

            if (relativeProfilePath.Length > 0)
            {
                Dictionary<string, object> entry = new Dictionary<string, object>();
                entry["profilePath"] = relativeProfilePath;
                perGame[game] = entry;
            }

            cfg["perGameProfiles"] = perGame;

            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            string path = Path.Combine(ScriptRunner.BaseDir, "game-mode-config.json");
            File.WriteAllText(path, ser.Serialize(cfg), new UTF8Encoding(true));
        }

        // Nome de arquivo seguro para profiles\<jogo>.json
        private static string ProfileFileNameForGame(string game)
        {
            return Regex.Replace(game, "[^A-Za-z0-9_-]", "_") + ".json";
        }

        private void CreateGameProfile(string game)
        {
            try
            {
                string dir = Path.Combine(ScriptRunner.BaseDir, "profiles");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string src = Path.Combine(ScriptRunner.BaseDir, "service-profile.game.json");
                string dst = Path.Combine(dir, ProfileFileNameForGame(game));
                File.Copy(src, dst, true);
                SavePerGameProfile(game, @"profiles\" + ProfileFileNameForGame(game));
                MessageBox.Show(
                    "Perfil personalizado criado para '" + game + "':\n" + dst +
                    "\n\nO runner usa este arquivo ao ativar o modo jogo com este jogo.\n" +
                    "Edite os serviços pelo menu (Editar perfil).",
                    "Perfil por jogo", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erro ao criar perfil:\n" + ex.Message,
                    "Perfil por jogo", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void PickGameProfile(string game)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Escolher arquivo de perfil do jogo";
                dlg.Filter = "JSON (*.json)|*.json|Todos os arquivos (*.*)|*.*";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    string dir = Path.Combine(ScriptRunner.BaseDir, "profiles");
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    string dst = Path.Combine(dir, ProfileFileNameForGame(game));
                    File.Copy(dlg.FileName, dst, true);
                    SavePerGameProfile(game, @"profiles\" + ProfileFileNameForGame(game));
                    MessageBox.Show("Perfil do jogo '" + game + "' atualizado:\n" + dst,
                        "Perfil por jogo", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Erro ao definir perfil:\n" + ex.Message,
                        "Perfil por jogo", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void EditGameProfile(string relativePath)
        {
            try
            {
                string full = Path.Combine(ScriptRunner.BaseDir, relativePath);
                if (!File.Exists(full))
                {
                    MessageBox.Show("Arquivo não encontrado:\n" + full,
                        "Perfil por jogo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = full,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erro ao abrir perfil:\n" + ex.Message,
                    "Perfil por jogo", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RemoveGameProfile(string game)
        {
            try
            {
                // Apenas desvincula do config - o arquivo em profiles\ fica como
                // rascunho (nenhuma exclusao automatica).
                SavePerGameProfile(game, "");
                MessageBox.Show("Perfil personalizado removido - '" + game +
                    "' volta a usar o perfil padrão.", "Perfil por jogo",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erro ao remover perfil:\n" + ex.Message,
                    "Perfil por jogo", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Executa app\Detect-Games.ps1 (Steam/Epic/Riot/aplicativos instalados) e importa
        /// os processos detectados na lista de jogos monitorados. Roda em thread para nao
        /// travar a GUI, igual a aba Servicos.
        /// </summary>
        private void DetectGames()
        {
            string scriptPath = Path.Combine(ScriptRunner.BaseDir, @"app\Detect-Games.ps1");
            if (!File.Exists(scriptPath))
            {
                MessageBox.Show("Script de detecção não encontrado:\n" + scriptPath,
                    "Jogos", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string jsonPath = Path.Combine(ScriptRunner.BaseDir, @"runtime\games-detected.json");
            try { if (File.Exists(jsonPath)) File.Delete(jsonPath); }
            catch { }

            btnDetectGames.Enabled = false;
            btnDetectGames.Text = "Detectando...";

            Thread t = new Thread(delegate()
            {
                string error = null;
                int detected = 0, added = 0, existing = 0;
                List<string> imported = new List<string>();
                HashSet<string> known = new HashSet<string>(games, StringComparer.OrdinalIgnoreCase);
                try
                {
                    int exit;
                    string cmd = string.Format("& '{0}' | Out-Null", scriptPath);
                    string output = ScriptRunner.RunPs(cmd, 120000, out exit);
                    if (exit != 0)
                        throw new InvalidOperationException("Falha na detecção:\n" + ScriptRunner.Tail(output, 5));
                    if (!File.Exists(jsonPath))
                        throw new InvalidOperationException("runtime\\games-detected.json não foi gerado.");

                    object root = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(jsonPath));
                    IList list = root as IList;
                    if (list != null)
                    {
                        foreach (object o in list)
                        {
                            Dictionary<string, object> d = o as Dictionary<string, object>;
                            if (d == null) continue;
                            detected++;

                            // Prefere o nome de processo derivado da pasta de instalacao;
                            // sem ele, usa o nome de exibicao do jogo.
                            string proc = Str(d, "ProcessName");
                            if (proc.Length == 0) proc = Str(d, "Name");
                            if (proc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                proc = proc.Substring(0, proc.Length - 4);
                            proc = proc.Trim();
                            if (proc.Length == 0) continue;

                            // HashSet cobre a lista atual E o proprio lote (evita
                            // duplicatas quando 2 fontes trazem o mesmo processo)
                            if (known.Contains(proc)) { existing++; continue; }
                            known.Add(proc);

                            imported.Add(proc);
                            added++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                Invoke((MethodInvoker)delegate
                {
                    btnDetectGames.Enabled = true;
                    btnDetectGames.Text = "Detectar jogos";

                    if (error != null)
                    {
                        MessageBox.Show(error, "Jogos", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    foreach (string p in imported) games.Add(p);
                    listGames.Items.Clear();
                    foreach (string g in games) listGames.Items.Add(g);

                    string msg = string.Format(
                        "Detectados: {0} jogos (Steam / Epic / Riot / aplicativos instalados).\n" +
                        "Adicionados à lista: {1}\nJá existentes: {2}\n\n" +
                        "Os nomes importados são de processo; ajuste manualmente se o jogo usar outro executável.",
                        detected, added, existing);

                    if (added > 0)
                    {
                        if (MessageBox.Show(msg + "\n\nSalvar a lista agora?", "Jogos",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                        {
                            SaveGames();
                        }
                    }
                    else
                    {
                        MessageBox.Show(msg, "Jogos", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void LoadLog()
        {
            try
            {
                if (!File.Exists(LogPath))
                {
                    txtLog.Text = "(Sem registros ainda - o runner gerará runtime\\game-mode.log ao alternar modos)";
                    return;
                }
                string[] lines = File.ReadAllLines(LogPath, Encoding.UTF8);
                int start = Math.Max(0, lines.Length - 300);
                StringBuilder sb = new StringBuilder();
                for (int i = start; i < lines.Length; i++) sb.AppendLine(lines[i]);
                txtLog.Text = sb.ToString();
                txtLog.SelectionStart = txtLog.TextLength;
                txtLog.ScrollToCaret();
            }
            catch (Exception ex)
            {
                txtLog.Text = "Erro ao ler log: " + ex.Message;
            }
        }

        private void ApplyAppIcon()
        {
            try
            {
                Icon appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (appIcon != null) { Icon = appIcon; }
            }
            catch { }
        }

        private static Image LoadLogo()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream("KrotonLogo.png"))
                {
                    if (s == null) return null;
                    return Image.FromStream(s);
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
