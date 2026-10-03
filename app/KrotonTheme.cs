using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Kroton
{
    /// <summary>
    /// Paleta e utilitarios visuais baseados na logo do Kroton.ia.
    /// Gradiente principal: Azul (#2196F3 / #0077EE) -> Teal/Ciano (#00C29A / #1DE9B6).
    /// Fundo escuro (Dark Navy): #0E1626 / #141E33.
    /// </summary>
    internal static class KrotonTheme
    {
        // Cores base da logo
        public static readonly Color GradientStart = Color.FromArgb(0, 119, 238);   // Azul vivo
        public static readonly Color GradientMid   = Color.FromArgb(0, 158, 251);   // Azul ciano
        public static readonly Color GradientEnd   = Color.FromArgb(0, 194, 154);   // Verde/Teal

        // Fundo escuro elegante (harmonizado com o azul escuro da logo)
        public static readonly Color BgDarkest     = Color.FromArgb(14, 22, 38);    // Fundo da barra superior
        public static readonly Color BgDark        = Color.FromArgb(18, 28, 48);    // Fundo da janela
        public static readonly Color BgCard        = Color.FromArgb(24, 38, 64);    // Fundo de cards/barras
        public static readonly Color BgCardLight   = Color.FromArgb(32, 50, 84);    // Hover / selecao
        public static readonly Color BorderColor   = Color.FromArgb(42, 68, 112);   // Bordas sutis

        // Textos
        public static readonly Color TextPrimary   = Color.FromArgb(240, 244, 250); // Branco suave
        public static readonly Color TextSecondary = Color.FromArgb(150, 175, 210); // Azul claro suave
        public static readonly Color TextMuted     = Color.FromArgb(100, 125, 160); // Desativado/detalhe

        // Acentos funcionais
        public static readonly Color AccentSuccess = Color.FromArgb(46, 204, 113);  // Normal / Ativo
        public static readonly Color AccentWarning = Color.FromArgb(241, 196, 15);  // Modo Jogo
        public static readonly Color AccentDanger  = Color.FromArgb(231, 76, 60);   // Parado / Bloqueado
        public static readonly Color AccentLocked  = Color.FromArgb(180, 100, 100); // Sistema protegido

        // Fontes padrao modernas
        public static readonly Font FontTitle      = new Font("Segoe UI", 12F, FontStyle.Bold);
        public static readonly Font FontSubtitle   = new Font("Segoe UI", 9F, FontStyle.Regular);
        public static readonly Font FontBody       = new Font("Segoe UI", 9F, FontStyle.Regular);
        public static readonly Font FontBodyBold   = new Font("Segoe UI", 9F, FontStyle.Bold);
        public static readonly Font FontSmall      = new Font("Segoe UI", 8F, FontStyle.Regular);

        /// <summary>Pinta um painel com o gradiente horizontal caracteristico da logo.</summary>
        public static void PaintGradientHeader(Graphics g, Rectangle rect)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;
            using (LinearGradientBrush brush = new LinearGradientBrush(
                rect, BgDarkest, Color.FromArgb(18, 38, 60), LinearGradientMode.Horizontal))
            {
                ColorBlend blend = new ColorBlend(3);
                blend.Colors = new[] {
                    Color.FromArgb(10, 18, 32),
                    Color.FromArgb(16, 32, 54),
                    Color.FromArgb(12, 28, 44)
                };
                blend.Positions = new[] { 0f, 0.5f, 1f };
                brush.InterpolationColors = blend;
                g.FillRectangle(brush, rect);
            }

            // Linha inferior luminosa com as cores da logo (azul -> verde)
            using (LinearGradientBrush lineBrush = new LinearGradientBrush(
                new Rectangle(rect.Left, rect.Bottom - 2, rect.Width, 2),
                GradientStart, GradientEnd, LinearGradientMode.Horizontal))
            {
                g.FillRectangle(lineBrush, rect.Left, rect.Bottom - 2, rect.Width, 2);
            }
        }

        /// <summary>Retangulo com cantos arredondados ( GraphicsPath p/ Region e Paint).</summary>
        public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int arcW = Math.Min(radius * 2, bounds.Width);
            int arcH = Math.Min(radius * 2, bounds.Height);
            if (arcW < 4 || arcH < 4)
            {
                path.AddRectangle(bounds);
                return path;
            }

            path.AddArc(bounds.X, bounds.Y, arcW, arcH, 180, 90);
            path.AddArc(bounds.Right - arcW, bounds.Y, arcW, arcH, 270, 90);
            path.AddArc(bounds.Right - arcW, bounds.Bottom - arcH, arcW, arcH, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - arcH, arcW, arcH, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>
        /// Aplica cantos arredondados a um controle via Region e renova no Resize.
        /// Chamar UMA vez por controle. Cantos mostram o BackColor do pai (fundo escuro).
        /// </summary>
        public static void RoundCorners(Control c, int radius)
        {
            if (c == null) return;
            ApplyRoundRegion(c, radius);
            c.Resize += delegate { ApplyRoundRegion(c, radius); };
        }

        private static void ApplyRoundRegion(Control c, int radius)
        {
            if (c.IsDisposed || c.Width < 4 || c.Height < 4) return;
            try
            {
                using (GraphicsPath path = RoundedRect(new Rectangle(0, 0, c.Width, c.Height), radius))
                {
                    Region old = c.Region;
                    c.Region = new Region(path);
                    if (old != null) old.Dispose();
                }
            }
            catch { }
        }

        /// <summary>Estiliza um botao com a aparencia moderna do Kroton.</summary>
        public static void StyleButton(Button btn, bool isPrimary = false)
        {
            RoundedButton rb = btn as RoundedButton;
            if (rb != null) rb.Primary = isPrimary;

            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderSize = 1;
            btn.Font = FontBodyBold;
            btn.Cursor = Cursors.Hand;

            if (isPrimary)
            {
                btn.BackColor = GradientStart;
                btn.ForeColor = Color.White;
                btn.FlatAppearance.BorderColor = GradientMid;
            }
            else
            {
                btn.BackColor = BgCard;
                btn.ForeColor = TextPrimary;
                btn.FlatAppearance.BorderColor = BorderColor;
            }

            btn.MouseEnter += delegate {
                if (isPrimary)
                    btn.BackColor = GradientMid;
                else
                    btn.BackColor = BgCardLight;
            };
            btn.MouseLeave += delegate {
                if (isPrimary)
                    btn.BackColor = GradientStart;
                else
                    btn.BackColor = BgCard;
            };
        }
    }

    /// <summary>
    /// Botao com cantos arredondados (8px), gradiente sutil de superficie, borda de
    /// 1px, hover via BackColor (StyleButton) e anel de foco. Pintura propria -
    /// substitui o render quadrado padrao do WinForms sem bibliotecas externas.
    /// </summary>
    internal class RoundedButton : Button
    {
        private const int Radius = 8;
        public bool Primary;

        public RoundedButton()
        {
            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Height = 27;
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color back = Enabled ? BackColor : KrotonTheme.BgCard;
            Color border = Enabled && Primary ? KrotonTheme.GradientMid : KrotonTheme.BorderColor;

            using (GraphicsPath path = KrotonTheme.RoundedRect(r, Radius))
            {
                // brilho sutil no topo (efeito de superficie moderna)
                Color top = back;
                if (Enabled)
                {
                    top = Color.FromArgb(
                        Math.Min(255, back.R + (255 - back.R) / 7),
                        Math.Min(255, back.G + (255 - back.G) / 7),
                        Math.Min(255, back.B + (255 - back.B) / 7));
                }
                using (LinearGradientBrush br = new LinearGradientBrush(r, top, back, LinearGradientMode.Vertical))
                {
                    g.FillPath(br, path);
                }
                using (Pen p = new Pen(border))
                {
                    g.DrawPath(p, path);
                }
            }

            Color fore = Enabled ? ForeColor : KrotonTheme.TextMuted;
            TextRenderer.DrawText(g, Text, Font, r, fore,
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis);

            if (Enabled && Focused)
            {
                using (GraphicsPath path = KrotonTheme.RoundedRect(
                    new Rectangle(3, 3, Width - 7, Height - 7), Radius - 2))
                using (Pen p = new Pen(KrotonTheme.TextSecondary) { DashStyle = DashStyle.Dot })
                {
                    g.DrawPath(p, path);
                }
            }
        }
    }
}
