import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register } from 'claude-code'

import type { CostReport, CostState } from '../types'
import { buildCard, MODEL_COLORS, tokens, usd } from './card'

const PANE = 'cost-meter'
const TITLE = 'Cost & hygiene'
const REFRESH_MS = 120_000

// Bump the tag when CostState's fields change: it declines what an earlier version stored.
const costState = atom(
  { plugin: 'cost-meter', key: 'state' } as const,
  { report: null, error: null, isLoading: false } as CostState,
  { shape: 'cost-state-2' },
)

// The card is an image: a fixed pixel width keeps its text at 1:1 instead of scaling with the pane.
// A width wider than the pane shrinks to fit it.
const cardWidth = (columns: number | undefined) => Math.min(760, Math.max(360, Math.round((columns ?? 60) * 8)))

async function refresh($: EngineInterface): Promise<void> {
  await update($, costState, s => ({ ...s, isLoading: true }))
  try {
    const env = await $.process.hookEnv()
    const script = `${env.CLAUDE_PLUGIN_ROOT}/hooks/cost.py`
    const root = await $.session.root()
    // The first read of large transcripts can be slow while the disk cache is cold.
    const ran = await $.process.run(['python3', '-I', script, root], { timeoutMs: 180_000 })
    if (ran.exitCode !== 0) throw new Error(ran.stderr.trim().split('\n').pop() || `exit ${ran.exitCode}`)
    const report = JSON.parse(ran.stdout) as CostReport
    await update($, costState, () => ({ report, error: null, isLoading: false }))
    $.ui.status(`$ ${usd(report.total)} · today ${usd(report.today)}`)
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err)
    await update($, costState, s => ({ ...s, error: message, isLoading: false }))
  }
}

export const register: Register = on => {
  on('session.start', async ($, e, next) => {
    const started = await next(e)
    await $.command.register({ name: 'project-cost', description: 'Show what this project has cost so far' })
    void $.ui.open({ id: PANE, title: TITLE })
    void refresh($)
    $.clock.every(REFRESH_MS, () => refresh($))
    return started
  })

  on('command.run', { command: 'project-cost' }, async $ => {
    await $.ui.open({ id: PANE, title: TITLE })
    await refresh($)
    const { report } = await read($, costState)
    return { text: report ? `Total ${usd(report.total)}, today ${usd(report.today)}.` : 'Cost pane opened.' }
  })

  on('ui.render', { component: 'Pane', requestId: PANE }, async ($, e) => {
    const elements = $.ui.resolve(e)
    const { Box, Text, Button } = elements
    const { report, error, isLoading } = await read($, costState)

    const footer = report && (
      <Box flexDirection="column" gap={1}>
        {report.unpriced.length > 0 && <Text size="footnote" color="warning">No price for: {report.unpriced.join(', ')}</Text>}
        {error && <Text size="footnote" color="error">Last refresh failed: {error}</Text>}
        <Box flexDirection="row" justifyContent="space-between" alignItems="center">
          <Text size="caption" dimColor>
            {isLoading ? 'refreshing…' : `estimate at API list prices · updated ${report.updatedAt.slice(11, 16)} UTC`}
          </Text>
          <Button label="Refresh" hotkey="r" onPress={() => refresh($)} />
        </Box>
      </Box>
    )

    if (!report) {
      return (
        <Box flexDirection="column" padding={1} gap={1}>
          <Text size="heading" weight="semibold">Cost</Text>
          <Text dimColor>{error ? `Could not read the transcripts: ${error}` : 'Reading session transcripts…'}</Text>
        </Box>
      )
    }

    // Every surface but the terminal draws Svg; its element table can still carry the key.
    if (e.surface !== 'terminal' && 'Svg' in elements) {
      const width = cardWidth(e.props.bodyColumns)
      const card = buildCard(report, width)
      return (
        <Box flexDirection="column" padding={1} gap={1}>
          <elements.Svg
            source={card.svg}
            width={width}
            height={card.height}
            alt={`Project cost ${usd(report.total)}, today ${usd(report.today)}`}
          />
          {footer}
        </Box>
      )
    }

    // The terminal has no Svg: the same figures as text.
    const hygiene = report.hygiene
    return (
      <Box flexDirection="column" padding={1} gap={1}>
        <Box flexDirection="column" paddingX={1} borderStyle="round" borderColor="claude">
          <Text bold color="claude">{usd(report.total)}</Text>
          <Text>today <Text bold>{usd(report.today)}</Text> <Text dimColor>· {report.sessions} sessions · {report.subagents} subagents</Text></Text>
        </Box>
        {report.models.map(m => (
          <Box key={m.model} flexDirection="column">
            <elements.Progress label={m.label} value={m.cost} max={Math.max(report.total, 0.01)} valueText={usd(m.cost)} color={MODEL_COLORS[m.model]} />
            <Text dimColor>out {tokens(m.output)} · cache read {tokens(m.cacheRead)}</Text>
          </Box>
        ))}
        <Text dimColor>main {usd(report.mainCost)} · subagents {usd(report.subagentCost)}</Text>
        {hygiene && (
          <elements.Progress
            label="Hygiene"
            value={Math.min(hygiene.projectSeconds, hygiene.projectThresholdSeconds)}
            max={hygiene.projectThresholdSeconds}
            valueText={`${Math.round(hygiene.projectSeconds / 60)}/${Math.round(hygiene.projectThresholdSeconds / 60)} min · ${hygiene.queueEvents} queued`}
          />
        )}
        {footer}
      </Box>
    )
  })
}
