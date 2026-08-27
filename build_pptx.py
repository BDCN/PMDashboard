"""Generates PMDashboard.pptx in the project root."""

from pptx import Presentation
from pptx.util import Inches, Pt, Emu
from pptx.dml.color import RGBColor
from pptx.enum.text import PP_ALIGN
from pptx.util import Inches, Pt
import os

# ── Brand colours ──────────────────────────────────────────────────────────────
IEM_RED    = RGBColor(0xC0, 0x31, 0x1E)   # IEM logo red
DARK_NAVY  = RGBColor(0x1E, 0x29, 0x3B)   # sidebar dark
MID_SLATE  = RGBColor(0x47, 0x55, 0x69)
LIGHT_BLUE = RGBColor(0xEF, 0xF6, 0xFF)
ACCENT_BLU = RGBColor(0x25, 0x63, 0xEB)
WHITE      = RGBColor(0xFF, 0xFF, 0xFF)
LIGHT_GREY = RGBColor(0xF1, 0xF5, 0xF9)
TEXT_DARK  = RGBColor(0x0F, 0x17, 0x2A)

SLIDE_W = Inches(13.33)
SLIDE_H = Inches(7.5)

prs = Presentation()
prs.slide_width  = SLIDE_W
prs.slide_height = SLIDE_H

blank_layout = prs.slide_layouts[6]  # completely blank

# ── Helpers ────────────────────────────────────────────────────────────────────

def add_rect(slide, left, top, width, height, fill_rgb, alpha=None):
    shape = slide.shapes.add_shape(1, left, top, width, height)  # MSO_SHAPE_TYPE.RECTANGLE = 1
    shape.line.fill.background()
    shape.line.width = 0
    fill = shape.fill
    fill.solid()
    fill.fore_color.rgb = fill_rgb
    return shape

def add_text_box(slide, text, left, top, width, height,
                 font_size=18, bold=False, color=TEXT_DARK,
                 align=PP_ALIGN.LEFT, wrap=True, italic=False):
    tb = slide.shapes.add_textbox(left, top, width, height)
    tf = tb.text_frame
    tf.word_wrap = wrap
    p = tf.paragraphs[0]
    p.alignment = align
    run = p.add_run()
    run.text = text
    run.font.size = Pt(font_size)
    run.font.bold = bold
    run.font.italic = italic
    run.font.color.rgb = color
    return tb

def add_bullet_slide(slide, title_text, bullets, accent=IEM_RED):
    """Dark left bar + title + bullet list."""
    # Left accent bar
    add_rect(slide, 0, 0, Inches(0.25), SLIDE_H, accent)
    # Background
    add_rect(slide, Inches(0.25), 0, SLIDE_W - Inches(0.25), SLIDE_H, WHITE)

    # Title area background stripe
    add_rect(slide, Inches(0.25), 0, SLIDE_W - Inches(0.25), Inches(1.35), LIGHT_GREY)

    # Title text
    add_text_box(slide, title_text,
                 left=Inches(0.6), top=Inches(0.22),
                 width=Inches(12.4), height=Inches(1.0),
                 font_size=28, bold=True, color=DARK_NAVY)

    # Bullets
    content_top = Inches(1.55)
    line_h      = Inches(0.58)
    for i, (bullet, sub) in enumerate(bullets):
        y = content_top + i * line_h
        # Bullet dot
        dot = slide.shapes.add_shape(9, Inches(0.55), y + Inches(0.13), Pt(10), Pt(10))
        dot.fill.solid(); dot.fill.fore_color.rgb = accent
        dot.line.fill.background()
        # Main bullet
        add_text_box(slide, bullet,
                     left=Inches(0.85), top=y,
                     width=Inches(12.0), height=Inches(0.35),
                     font_size=15, bold=True, color=DARK_NAVY)
        if sub:
            add_text_box(slide, sub,
                         left=Inches(0.95), top=y + Inches(0.30),
                         width=Inches(11.9), height=Inches(0.30),
                         font_size=12, bold=False, color=MID_SLATE)

# ═══════════════════════════════════════════════════════════════════════════════
# SLIDE 1 — Title
# ═══════════════════════════════════════════════════════════════════════════════
s = prs.slides.add_slide(blank_layout)

# Full background: dark navy
add_rect(s, 0, 0, SLIDE_W, SLIDE_H, DARK_NAVY)

# Red accent band (left third)
add_rect(s, 0, 0, Inches(4.8), SLIDE_H, IEM_RED)

# White card on left
add_rect(s, Inches(0.35), Inches(1.8), Inches(4.1), Inches(3.9), WHITE)

# IEM text (replacing logo since we can't embed the PNG easily)
add_text_box(s, "IEM", Inches(0.6), Inches(2.1),
             Inches(3.6), Inches(1.1),
             font_size=72, bold=True, color=IEM_RED)

add_text_box(s, "Integrated Electrical Management",
             Inches(0.6), Inches(3.2), Inches(3.6), Inches(0.5),
             font_size=11, bold=False, color=MID_SLATE)

# Title / subtitle on dark right
add_text_box(s, "PM Dashboard",
             Inches(5.3), Inches(1.8), Inches(7.7), Inches(1.2),
             font_size=44, bold=True, color=WHITE)

add_text_box(s, "Resource Management Platform",
             Inches(5.3), Inches(2.95), Inches(7.7), Inches(0.6),
             font_size=22, bold=False, color=RGBColor(0x93, 0xC5, 0xFD))

add_text_box(s, "Streamlining Project Manager workload assignment\n"
                "across multi-location youth football leagues and\n"
                "enterprise infrastructure programs.",
             Inches(5.3), Inches(3.65), Inches(7.7), Inches(1.4),
             font_size=13, bold=False, color=RGBColor(0xCB, 0xD5, 0xE1))

add_text_box(s, "Confidential — IEM Internal",
             Inches(5.3), Inches(6.8), Inches(7.7), Inches(0.4),
             font_size=10, bold=False, color=RGBColor(0x47, 0x55, 0x69),
             align=PP_ALIGN.RIGHT)

# ═══════════════════════════════════════════════════════════════════════════════
# SLIDE 2 — The Problem
# ═══════════════════════════════════════════════════════════════════════════════
s = prs.slides.add_slide(blank_layout)
add_bullet_slide(s, "The Problem We Needed to Solve", [
    ("No visibility into PM workload",
     "PMs were assigned sites ad-hoc with no central view of who was over- or under-utilized."),
    ("Manual, error-prone assignment process",
     "Assignments were tracked in spreadsheets — difficult to keep current across a large team."),
    ("No consistent scoring methodology",
     "Different sites and ROMPs carry very different complexity; a simple site count hides the real burden."),
    ("Multi-user data conflicts",
     "Multiple people editing the same spreadsheet led to overwritten data and version confusion."),
    ("No audit trail or structured hierarchy",
     "Locations, sites, and ROMPs had no formal parent–child structure; relationships were implicit."),
])

# ═══════════════════════════════════════════════════════════════════════════════
# SLIDE 3 — What is PMDashboard?
# ═══════════════════════════════════════════════════════════════════════════════
s = prs.slides.add_slide(blank_layout)
add_rect(s, 0, 0, SLIDE_W, SLIDE_H, WHITE)
add_rect(s, 0, 0, SLIDE_W, Inches(1.35), DARK_NAVY)
add_rect(s, 0, 0, Inches(0.25), SLIDE_H, IEM_RED)

add_text_box(s, "What is PM Dashboard?",
             Inches(0.6), Inches(0.22), Inches(12.4), Inches(1.0),
             font_size=28, bold=True, color=WHITE)

# Three columns
col_w = Inches(3.9)
cols = [
    ("🏢", "Data Hierarchy",
     "Locations (airports) → Sites → ROMPs\nEvery ROMP is tracked with status, equipment type, and scoring."),
    ("📊", "Workload Scoring",
     "Multiplicative formula:\nromp_score = activity × equipment\npm_load = site_factor × Σ scores"),
    ("👥", "PM Assignment",
     "Ranked candidate list based on region match, location familiarity, and remaining capacity.\nMulti-site bulk assignment in one click."),
]
for i, (icon, title, desc) in enumerate(cols):
    x = Inches(0.6) + i * (col_w + Inches(0.1))
    add_rect(s, x, Inches(1.6), col_w, Inches(5.5), LIGHT_GREY)
    add_text_box(s, icon,  x + Inches(0.2), Inches(1.8),  col_w - Inches(0.4), Inches(0.6), font_size=32)
    add_text_box(s, title, x + Inches(0.2), Inches(2.5),  col_w - Inches(0.4), Inches(0.55),
                 font_size=15, bold=True, color=DARK_NAVY)
    add_text_box(s, desc,  x + Inches(0.2), Inches(3.15), col_w - Inches(0.4), Inches(3.7),
                 font_size=12, color=MID_SLATE)

# ═══════════════════════════════════════════════════════════════════════════════
# SLIDE 4 — Data Model
# ═══════════════════════════════════════════════════════════════════════════════
s = prs.slides.add_slide(blank_layout)
add_bullet_slide(s, "Structured Data Hierarchy", [
    ("Locations  (e.g. IAD, JAN, ALE)",
     "3-letter airport codes with East/West geographic region. Parent of all sites."),
    ("Sites  (e.g. IAD045, ALE060)",
     "Each site belongs to one location and is assigned to one PM. Tracks status: Ordered / Active / Complete."),
    ("ROMPs  (e.g. ROMP01, ROMP02)",
     "Root Of Measurement Points — the granular work unit. Each ROMP has a status and multiple equipment types."),
    ("Equipment Types  (PAX, LV, MV, PDC)",
     "Directly assigned to each ROMP. Weight: PAX=1.6 · LV=1.3 · MV=1.0 · PDC=0.6. Drives the score."),
    ("Status Lifecycle  →  Ordered · OFCI · Construction · CX · Complete",
     "Activity multipliers: 0.4 · 0.6 · 1.0 · 1.2 · 0.1. Complete ROMPs are excluded from workload totals."),
])

# ═══════════════════════════════════════════════════════════════════════════════
# SLIDE 5 — Scoring Formula
# ═══════════════════════════════════════════════════════════════════════════════
s = prs.slides.add_slide(blank_layout)
add_rect(s, 0, 0, SLIDE_W, SLIDE_H, WHITE)
add_rect(s, 0, 0, SLIDE_W, Inches(1.35), DARK_NAVY)
add_rect(s, 0, 0, Inches(0.25), SLIDE_H, IEM_RED)

add_text_box(s, "The Workload Scoring Model",
             Inches(0.6), Inches(0.22), Inches(12.4), Inches(1.0),
             font_size=28, bold=True, color=WHITE)

# Formula box
add_rect(s, Inches(0.6), Inches(1.5), Inches(12.1), Inches(2.7), DARK_NAVY)
formula = (
    "equip_weight_sum  =  Σ weight[t]   for each equipment type t on the ROMP\n\n"
    "romp_score        =  activity_weight  ×  equip_weight_sum\n\n"
    "pm_load           =  site_factor  ×  Σ romp_scores\n"
    "                          where  site_factor = 1 + 0.05 × distinct_sites\n\n"
    "utilization       =  pm_load  ÷  Target Capacity (259.2)\n\n"
    "availability      =  max(0,  1 − utilization)"
)
add_text_box(s, formula,
             Inches(0.9), Inches(1.65), Inches(11.6), Inches(2.4),
             font_size=12, bold=False, color=RGBColor(0x93, 0xC5, 0xFD),
             italic=True)

# Why this matters
add_text_box(s, "Why a multiplicative formula?",
             Inches(0.6), Inches(4.4), Inches(12.1), Inches(0.4),
             font_size=14, bold=True, color=DARK_NAVY)

reasons = [
    "A CX-status ROMP with PAX equipment (1.2 × 1.6 = 1.92) correctly outweighs an Ordered ROMP with no equipment (0.4 × 0 = 0).",
    "Site overhead (5% per site) accounts for the management burden of juggling many separate locations.",
    "Target Capacity of 259.2 reflects actual measured average PM loads — the default sits at 50% of the slider maximum.",
]
for i, r in enumerate(reasons):
    add_text_box(s, f"• {r}",
                 Inches(0.8), Inches(4.9) + i * Inches(0.55),
                 Inches(11.8), Inches(0.5),
                 font_size=12, color=MID_SLATE)

# ═══════════════════════════════════════════════════════════════════════════════
# SLIDE 6 — Workload Page
# ═══════════════════════════════════════════════════════════════════════════════
s = prs.slides.add_slide(blank_layout)
add_bullet_slide(s, "Workload View — Instant PM Health at a Glance", [
    ("Horizontal bar chart — one row per active PM",
     "Sorted by utilization. Bar colour: blue (below ideal) → green (on target) → red (over capacity)."),
    ("Score mode KPI cards",
     "Overloaded / Near Full / Has Capacity — thresholds configurable in Settings."),
    ("Label on each bar",
     "Score: 460.94 ⚠  ·  2560.8% util  ·  0.0% avail  ·  28 sites / 259 ROMPs"),
    ("Hover tooltip",
     "Per-status ROMP counts + PM Load, Utilization %, Availability %, and overall Status."),
    ("Click any row → PM Detail page",
     "Drill into the individual PM's score breakdown, site list, and per-site actions."),
], accent=ACCENT_BLU)

# ═══════════════════════════════════════════════════════════════════════════════
# SLIDE 7 — PM Detail
# ═══════════════════════════════════════════════════════════════════════════════
s = prs.slides.add_slide(blank_layout)
add_bullet_slide(s, "PM Detail — Full Breakdown for Every Project Manager", [
    ("KPI cards: Active Sites · Completed Sites · Active ROMPs · Total ROMPs · PM Load",
     "PM Load card also shows availability % and status label (Has Capacity / Near Full / Overloaded)."),
    ("ROMP Status Breakdown",
     "Bar chart per status showing count, activity multiplier, and total score contribution."),
    ("Score formula footer",
     "ROMP sum: 166.26  ×  site factor 2.35  =  PM Load: 390.71"),
    ("Assigned Sites table — active sites only, with per-site actions",
     "Checkbox for bulk unassign · Clickable site name → drill to ROMs · Edit button · Unassign button."),
    ("Sites → ROMs navigation with Back button",
     "Click a site name to view its ROMs; ← Back button returns directly to the PM Detail page."),
])

# ═══════════════════════════════════════════════════════════════════════════════
# SLIDE 8 — Assignment
# ═══════════════════════════════════════════════════════════════════════════════
s = prs.slides.add_slide(blank_layout)
add_bullet_slide(s, "Smart PM Assignment", [
    ("Geographic region badge",
     "East / West badge shown next to the selected location — instantly confirms region match."),
    ("Multi-select site list",
     "Ctrl+Click or Shift+Click to select multiple sites. All / Unassigned / Clear quick-select buttons."),
    ("Ranked PM candidates",
     "Scored by: Region Match · Location Familiarity · Remaining Capacity. Rules configurable in Settings."),
    ("Bulk assignment",
     "Clicking Assign → assigns the chosen PM to ALL selected sites in a single action."),
    ("Override mode",
     "Bypass the ranked list and assign any PM directly from the override dropdown."),
], accent=RGBColor(0x16, 0xA3, 0x4A))

# ═══════════════════════════════════════════════════════════════════════════════
# SLIDE 9 — Database & Sync
# ═══════════════════════════════════════════════════════════════════════════════
s = prs.slides.add_slide(blank_layout)
add_rect(s, 0, 0, SLIDE_W, SLIDE_H, WHITE)
add_rect(s, 0, 0, SLIDE_W, Inches(1.35), DARK_NAVY)
add_rect(s, 0, 0, Inches(0.25), SLIDE_H, IEM_RED)

add_text_box(s, "Three Database Modes — Built for Any Team Size",
             Inches(0.6), Inches(0.22), Inches(12.4), Inches(1.0),
             font_size=28, bold=True, color=WHITE)

modes = [
    ("💻", "Local", LIGHT_GREY,
     "Each machine stores data in its own SQLite file.\nZero setup. Best for single-user use."),
    ("🌐", "Network", RGBColor(0xEF, 0xF6, 0xFF),
     "Shared SQLite file on a UNC network drive.\nTravel Sync: download before travel, merge after return.\nLastModified timestamps resolve conflicts."),
    ("☁️", "Cloud  (MariaDB)", RGBColor(0xF0, 0xFD, 0xF4),
     "Local-first: all reads/writes use local SQLite (fast).\nBackground sync every 30 s pushes/pulls changes.\nImmediate push on every save. Multi-user real-time."),
]
for i, (icon, title, bg, desc) in enumerate(modes):
    x = Inches(0.6) + i * Inches(4.2)
    add_rect(s, x, Inches(1.6), Inches(3.9), Inches(5.5), bg)
    add_text_box(s, icon,  x + Inches(0.2), Inches(1.8), Inches(3.5), Inches(0.6), font_size=32)
    add_text_box(s, title, x + Inches(0.2), Inches(2.5), Inches(3.5), Inches(0.5),
                 font_size=16, bold=True, color=DARK_NAVY)
    add_text_box(s, desc,  x + Inches(0.2), Inches(3.1), Inches(3.5), Inches(3.7),
                 font_size=12, color=MID_SLATE)

# ═══════════════════════════════════════════════════════════════════════════════
# SLIDE 10 — Configuration
# ═══════════════════════════════════════════════════════════════════════════════
s = prs.slides.add_slide(blank_layout)
add_bullet_slide(s, "Highly Configurable — No Hard-Coded Assumptions", [
    ("Scoring parameters — adjustable via sliders",
     "Site overhead · Target Capacity · Near Full threshold · Overload threshold. Defaults sit at 50% of each slider."),
    ("Status weights — drag slider to adjust activity multipliers",
     "Each ROMP status (Ordered, OFCI, Construction, CX, Complete) has a configurable multiplier (0 → 2.4)."),
    ("Equipment category weights — drag slider",
     "PAX, LV, MV, PDC weights are configurable (0 → 3.2). Add custom categories with keyword matching."),
    ("Admin-locked sections (single password)",
     "Source Data format and Database mode are greyed out until unlocked — prevents accidental changes."),
    ("Assignment rules — reorderable priority list",
     "Region Match · Location Familiarity · Capacity Availability. Custom rules can be added and weighted."),
], accent=RGBColor(0x93, 0x33, 0xEA))

# ═══════════════════════════════════════════════════════════════════════════════
# SLIDE 11 — Benefits
# ═══════════════════════════════════════════════════════════════════════════════
s = prs.slides.add_slide(blank_layout)
add_rect(s, 0, 0, SLIDE_W, SLIDE_H, WHITE)
add_rect(s, 0, 0, SLIDE_W, Inches(1.35), DARK_NAVY)
add_rect(s, 0, 0, Inches(0.25), SLIDE_H, IEM_RED)

add_text_box(s, "Key Benefits",
             Inches(0.6), Inches(0.22), Inches(12.4), Inches(1.0),
             font_size=28, bold=True, color=WHITE)

benefits = [
    ("⚡", "Instant visibility",       "See every PM's workload, score, and availability in a single bar-chart view."),
    ("🎯", "Data-driven assignment",   "Ranked candidates replace gut-feel — region, familiarity, and capacity are scored objectively."),
    ("📐", "Consistent scoring",       "The multiplicative formula ensures a CX ROMP with PAX equipment always outweighs a simple Ordered entry."),
    ("🔄", "Real-time collaboration",  "Cloud mode keeps all users' local databases in sync automatically — no shared spreadsheet conflicts."),
    ("✈️", "Offline / travel-ready",   "Travel sync downloads the network DB before a trip and merges changes back on return."),
    ("⚙️", "Zero-assumption config",   "Every weight, threshold, and rule is adjustable without touching code."),
]
cols = 3
for i, (icon, title, desc) in enumerate(benefits):
    row, col = divmod(i, cols)
    x = Inches(0.6)  + col * Inches(4.2)
    y = Inches(1.55) + row * Inches(2.55)
    add_rect(s, x, y, Inches(3.95), Inches(2.35), LIGHT_GREY)
    add_text_box(s, icon,  x + Inches(0.15), y + Inches(0.1),  Inches(0.6),  Inches(0.55), font_size=24)
    add_text_box(s, title, x + Inches(0.75), y + Inches(0.1),  Inches(3.0),  Inches(0.45),
                 font_size=13, bold=True, color=DARK_NAVY)
    add_text_box(s, desc,  x + Inches(0.15), y + Inches(0.65), Inches(3.65), Inches(1.55),
                 font_size=11, color=MID_SLATE)

# ═══════════════════════════════════════════════════════════════════════════════
# SLIDE 12 — Summary / Closing
# ═══════════════════════════════════════════════════════════════════════════════
s = prs.slides.add_slide(blank_layout)
add_rect(s, 0, 0, SLIDE_W, SLIDE_H, DARK_NAVY)
add_rect(s, 0, 0, Inches(0.25), SLIDE_H, IEM_RED)

add_text_box(s, "PM Dashboard",
             Inches(1.5), Inches(1.6), Inches(10.5), Inches(1.1),
             font_size=42, bold=True, color=WHITE, align=PP_ALIGN.CENTER)

add_text_box(s, "One platform. Every PM. Every site. Every ROMP.",
             Inches(1.5), Inches(2.7), Inches(10.5), Inches(0.65),
             font_size=20, bold=False, color=RGBColor(0x93, 0xC5, 0xFD),
             align=PP_ALIGN.CENTER)

summary_points = [
    "Structured hierarchy: Locations → Sites → ROMPs → Equipment Types",
    "Multiplicative scoring model — activity weight × equipment weight × site factor",
    "Instant workload visibility with configurable Overloaded / Near Full / Has Capacity thresholds",
    "Data-driven ranked PM assignment with bulk multi-site support",
    "Local · Network · Cloud database modes — works for one user or the entire team",
    "Fully configurable scoring, statuses, equipment, and assignment rules",
]
for i, point in enumerate(summary_points):
    add_text_box(s, f"✓  {point}",
                 Inches(2.0), Inches(3.55) + i * Inches(0.52),
                 Inches(9.5), Inches(0.48),
                 font_size=13, bold=False, color=RGBColor(0xCB, 0xD5, 0xE1))

add_text_box(s, "IEM  ·  Confidential",
             Inches(0.6), Inches(7.1), Inches(12.0), Inches(0.3),
             font_size=10, color=RGBColor(0x47, 0x55, 0x69),
             align=PP_ALIGN.CENTER)

# ── Save ───────────────────────────────────────────────────────────────────────
out = os.path.join(os.path.dirname(__file__), "PMDashboard_Overview.pptx")
prs.save(out)
print(f"Saved: {out}")
