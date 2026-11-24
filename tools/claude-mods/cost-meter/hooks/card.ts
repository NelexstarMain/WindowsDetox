import type { CostReport, Hygiene } from '../types'

// The card is drawn as an image, so it carries its own palette instead of following the app theme.
const BG = '#1C1B19'
const EDGE = '#FFFFFF14'
const TEXT = '#F5F4EF'
const MUTED = '#A3A199'
const FAINT = '#6E6C66'
const ACCENT = '#D97757'
const GOOD = '#8FB573'
const WARN = '#E0A458'
export const MODEL_COLORS: Record<string, string> = {
  'claude-opus-5-5': '#D97757',
  'claude-sonnet-5-5': '#6A9BCC',
  'claude-haiku-5-5': '#8FB573',
}
const WEEKDAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']

const PAD = 20
const FONT = "font-family=\"-apple-system,BlinkMacSystemFont,'Segoe UI',Inter,system-ui,sans-serif\""

export const usd = (n: number) => (n >= 100 ? `$${n.toFixed(0)}` : `$${n.toFixed(2)}`)
export const tokens = (n: number) =>
  n >= 1e9 ? `${(n / 1e9).toFixed(1)}B` : n >= 1e6 ? `${(n / 1e6).toFixed(1)}M` : n >= 1e3 ? `${(n / 1e3).toFixed(0)}k` : `${n}`

const esc = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')

function text(x: number, y: number, body: string, attrs: string): string {
  return `<text x="${x.toFixed(1)}" y="${y.toFixed(1)}" ${attrs}>${esc(body)}</text>`
}

function caption(x: number, y: number, body: string): string {
  return text(x, y, body, `font-size="10" font-weight="600" letter-spacing="1.1" fill="${FAINT}"`)
}

function dayChart(days: CostReport['days'], top: number, W: number): string {
  const chartTop = top + 22
  const chartH = 74
  const base = chartTop + chartH
  const gap = 8
  const barW = (W - PAD * 2 - gap * (days.length - 1)) / days.length
  const peak = Math.max(...days.map(d => d.cost), 0.01)
  const parts = [caption(PAD, top + 8, 'LAST 7 DAYS')]
  parts.push(`<line x1="${PAD}" y1="${base + 0.5}" x2="${W - PAD}" y2="${base + 0.5}" stroke="${EDGE}"/>`)
  days.forEach((d, i) => {
    const isToday = i === days.length - 1
    const x = PAD + i * (barW + gap)
    const h = d.cost > 0 ? Math.max(3, (d.cost / peak) * (chartH - 16)) : 0
    if (h > 0) {
      parts.push(
        `<rect x="${x.toFixed(1)}" y="${(base - h).toFixed(1)}" width="${barW.toFixed(1)}" height="${h.toFixed(1)}" rx="4" fill="${isToday ? 'url(#today)' : ACCENT}" fill-opacity="${isToday ? 1 : 0.55}"/>`,
      )
      const label = d.cost >= 10 ? d.cost.toFixed(0) : d.cost.toFixed(1)
      parts.push(text(x + barW / 2, base - h - 5, `$${label}`, `font-size="9" text-anchor="middle" fill="${isToday ? TEXT : MUTED}"`))
    } else {
      parts.push(`<rect x="${x.toFixed(1)}" y="${base - 2}" width="${barW.toFixed(1)}" height="2" rx="1" fill="${FAINT}" fill-opacity="0.4"/>`)
    }
    const day = isToday ? 'Today' : WEEKDAYS[new Date(`${d.date}T00:00:00Z`).getUTCDay()] ?? d.date.slice(5)
    parts.push(
      text(x + barW / 2, base + 15, day, `font-size="10" text-anchor="middle" fill="${isToday ? TEXT : MUTED}" font-weight="${isToday ? 600 : 400}"`),
    )
  })
  return parts.join('')
}

const minutes = (s: number) => (s >= 3600 ? `${(s / 3600).toFixed(1)} h` : `${Math.round(s / 60)} min`)

// What the hooks wait for before the background CLAUDE.md refresh: enough busy time and at least one queued event.
function hygieneState(h: Hygiene): { line: string; color: string } {
  const left = h.projectThresholdSeconds - h.projectSeconds
  if (left > 0) return { line: `${minutes(left)} of work until the next CLAUDE.md refresh`, color: MUTED }
  if (h.queueEvents === 0) return { line: 'Time reached · waits for a key moment to be queued', color: WARN }
  return { line: 'Due · runs when the next turn ends', color: GOOD }
}

function hygieneSection(parts: string[], h: Hygiene, top: number, W: number): number {
  let y = top + 18
  parts.push(`<line x1="${PAD}" y1="${y}" x2="${W - PAD}" y2="${y}" stroke="${EDGE}"/>`)
  y += 26
  parts.push(caption(PAD, y, 'CLAUDE CODE HYGIENE'))
  const status = h.lastStatus ? `last run ${h.lastRunAt ? h.lastRunAt.slice(5, 16).replace('T', ' ') : '?'} · ${h.lastStatus}` : 'never run'
  parts.push(text(W - PAD, y, status, `font-size="10" text-anchor="end" fill="${FAINT}"`))
  y += 26
  const barWidth = W - PAD * 2
  const meter = (label: string, value: number, max: number, color: string) => {
    const share = Math.min(1, value / Math.max(1, max))
    parts.push(text(PAD, y, label, `font-size="12" font-weight="600" fill="${TEXT}"`))
    parts.push(
      `<text x="${W - PAD}" y="${y}" font-size="12" text-anchor="end" fill="${TEXT}" font-weight="600">${esc(minutes(value))}<tspan fill="${MUTED}" font-weight="400"> / ${esc(minutes(max))}</tspan></text>`,
    )
    y += 9
    parts.push(`<rect x="${PAD}" y="${y}" width="${barWidth}" height="6" rx="3" fill="${FAINT}" fill-opacity="0.25"/>`)
    if (share > 0) parts.push(`<rect x="${PAD}" y="${y}" width="${Math.max(6, share * barWidth).toFixed(1)}" height="6" rx="3" fill="${color}"/>`)
    y += 26
  }
  const state = hygieneState(h)
  meter('Project · CLAUDE.md refresh', h.projectSeconds, h.projectThresholdSeconds, state.color === MUTED ? GOOD : state.color)
  parts.push(text(PAD, y - 8, `${state.line} · ${h.queueEvents} queued ${h.queueEvents === 1 ? 'event' : 'events'}`, `font-size="10.5" fill="${state.color}"`))
  y += 14
  meter('Global · preferences consolidation', h.globalSeconds, h.globalThresholdSeconds, MUTED)
  return y - 12
}

export function buildCard(report: CostReport, W: number): { svg: string; height: number } {
  const parts: string[] = []

  // Header: the total, today and how much work it covers.
  parts.push(caption(PAD, 32, 'PROJECT COST'))
  parts.push(`<rect x="${W - PAD - 62}" y="20" width="62" height="18" rx="9" fill="none" stroke="${EDGE}"/>`)
  parts.push(text(W - PAD - 31, 32.5, 'estimate', `font-size="10" text-anchor="middle" fill="${MUTED}"`))
  parts.push(text(PAD - 1, 78, usd(report.total), `font-size="40" font-weight="700" fill="${TEXT}" letter-spacing="-1"`))
  parts.push(
    `<text x="${PAD}" y="102" font-size="12" fill="${MUTED}">today <tspan fill="${ACCENT}" font-weight="600">${esc(usd(report.today))}</tspan></text>`,
  )
  parts.push(text(W - PAD, 102, `${report.sessions} sessions · ${report.subagents} subagents`, `font-size="11" text-anchor="end" fill="${FAINT}"`))

  // Spend per day.
  parts.push(dayChart(report.days, 126, W))
  let y = 252
  parts.push(`<line x1="${PAD}" y1="${y}" x2="${W - PAD}" y2="${y}" stroke="${EDGE}"/>`)

  // Spend per model: one stacked bar, then a row per model.
  y += 26
  parts.push(caption(PAD, y, 'BY MODEL'))
  y += 12
  const total = Math.max(report.total, 0.0001)
  const barWidth = W - PAD * 2
  parts.push(`<clipPath id="stack"><rect x="${PAD}" y="${y}" width="${barWidth}" height="8" rx="4"/></clipPath>`)
  parts.push(`<rect x="${PAD}" y="${y}" width="${barWidth}" height="8" rx="4" fill="${FAINT}" fill-opacity="0.25"/>`)
  let x = PAD
  const segments: string[] = []
  for (const m of report.models) {
    const w = (m.cost / total) * barWidth
    segments.push(`<rect x="${x.toFixed(1)}" y="${y}" width="${Math.max(0, w - 1).toFixed(1)}" height="8" fill="${MODEL_COLORS[m.model] ?? MUTED}"/>`)
    x += w
  }
  parts.push(`<g clip-path="url(#stack)">${segments.join('')}</g>`)
  y += 30
  for (const m of report.models) {
    const pct = (m.cost / total) * 100
    const share = pct > 0 && pct < 1 ? '<1' : `${Math.round(pct)}`
    parts.push(`<circle cx="${PAD + 4}" cy="${y - 4}" r="4" fill="${MODEL_COLORS[m.model] ?? MUTED}"/>`)
    parts.push(text(PAD + 16, y, m.label, `font-size="13" font-weight="600" fill="${TEXT}"`))
    parts.push(text(W - PAD, y, usd(m.cost), `font-size="13" font-weight="600" text-anchor="end" fill="${TEXT}"`))
    parts.push(
      text(
        PAD + 16,
        y + 17,
        `${share}% · ${tokens(m.output)} out · ${tokens(m.cacheWrite)} cache write · ${tokens(m.cacheRead)} cache read`,
        `font-size="10.5" fill="${MUTED}"`,
      ),
    )
    y += 42
  }

  // Who spent it: the main thread or subagents.
  y -= 6
  parts.push(`<line x1="${PAD}" y1="${y}" x2="${W - PAD}" y2="${y}" stroke="${EDGE}"/>`)
  y += 24
  const subShare = Math.round((report.subagentCost / total) * 100)
  parts.push(text(PAD, y, 'Main thread', `font-size="11" fill="${MUTED}"`))
  parts.push(text(W - PAD, y, 'Subagents', `font-size="11" text-anchor="end" fill="${MUTED}"`))
  y += 18
  parts.push(text(PAD, y, usd(report.mainCost), `font-size="15" font-weight="600" fill="${TEXT}"`))
  parts.push(
    `<text x="${W - PAD}" y="${y}" font-size="15" font-weight="600" text-anchor="end" fill="${TEXT}">${esc(usd(report.subagentCost))} <tspan font-size="11" font-weight="400" fill="${MUTED}">${subShare}%</tspan></text>`,
  )
  y += 12
  const mainW = ((100 - subShare) / 100) * barWidth
  parts.push(`<rect x="${PAD}" y="${y}" width="${barWidth}" height="6" rx="3" fill="${FAINT}" fill-opacity="0.25"/>`)
  parts.push(`<rect x="${PAD}" y="${y}" width="${Math.max(0, mainW - 1).toFixed(1)}" height="6" rx="3" fill="${TEXT}" fill-opacity="0.8"/>`)
  parts.push(`<rect x="${(PAD + mainW + 1).toFixed(1)}" y="${y}" width="${Math.max(0, barWidth - mainW - 1).toFixed(1)}" height="6" rx="3" fill="${ACCENT}"/>`)
  y += 6
  if (report.hygiene) y = hygieneSection(parts, report.hygiene, y, W)
  const height = Math.round(y + PAD)

  const defs =
    `<defs><linearGradient id="today" x1="0" y1="0" x2="0" y2="1">` +
    `<stop offset="0" stop-color="#E8956F"/><stop offset="1" stop-color="${ACCENT}"/></linearGradient></defs>`
  const svg =
    `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${height}" viewBox="0 0 ${W} ${height}" ${FONT}>` +
    defs +
    `<rect x="0.5" y="0.5" width="${W - 1}" height="${height - 1}" rx="16" fill="${BG}" stroke="${EDGE}"/>` +
    parts.join('') +
    `</svg>`
  return { svg, height }
}
