import { expect, test } from 'claude-code/testing'

import type { CostState } from '../types'
import { buildCard } from './card'

const PANE = {
  plugin: 'cost-meter',
  component: 'Pane',
  requestId: 'cost-meter',
  props: {
    title: 'Cost & hygiene',
    isFocused: false,
    bodyColumns: 60,
    placement: 'dock',
    scroll: { offset: 0, bodyRows: 40 },
    view: {},
  },
} as const

const STORED: CostState = {
  report: {
    total: 283.64,
    today: 3.65,
    mainCost: 133.14,
    subagentCost: 150.5,
    sessions: 4,
    subagents: 64,
    days: [
      { date: '2026-11-23', cost: 43.43 },
      { date: '2026-11-24', cost: 3.65 },
    ],
    models: [
      { model: 'claude-sonnet-5-5', label: 'Sonnet 5.5', cost: 164.83, input: 6395, output: 5077148, cacheWrite: 17716249, cacheRead: 666642157, messages: 3154 },
      { model: 'claude-haiku-5-5', label: 'Haiku 5.5', cost: 0.1, input: 2, output: 280, cacheWrite: 495075, cacheRead: 62786, messages: 1 },
    ],
    unpriced: [],
    hygiene: {
      projectSeconds: 2264,
      projectThresholdSeconds: 2400,
      globalSeconds: 4748,
      globalThresholdSeconds: 36000,
      queueEvents: 0,
      lastRunAt: '2026-11-23T07:38:39Z',
      lastStatus: 'applied',
    },
    updatedAt: '2026-11-24T09:07:54+00:00',
  },
  error: null,
  isLoading: false,
}

const SEEDED = () => ({ value: { value: { shape: 'cost-state-2', value: STORED }, version: 1 } })

test('desktop draws the card image with the refresh row', async ($, on) => {
  on('state.get', SEEDED)
  const ui = await $.ui.mount({ ...PANE, surface: 'desktop' })

  expect(await ui.find({ type: 'Svg' })).toBeTruthy()
  expect((await ui.find({ type: 'Text', text: /estimate at API list prices/ }))).toBeTruthy()
  await ui.unmount()
})

test('the terminal shows the same figures as text', async ($, on) => {
  on('state.get', SEEDED)
  const ui = await $.ui.mount({ ...PANE, surface: 'terminal' })

  expect((await ui.find({ type: 'Text', text: /^\$284$/ }))?.text).toBe('$284')
  expect((await ui.find({ type: 'Text', text: /subagents \$151/ }))).toBeTruthy()
  await ui.unmount()
})

test('the card says when hygiene has the time but no queued event', () => {
  const report = { ...STORED.report!, hygiene: { ...STORED.report!.hygiene!, projectSeconds: 2500 } }
  const { svg } = buildCard(report, 480)

  expect(svg).toContain('Time reached · waits for a key moment to be queued')
  expect(svg).toContain('&lt;1%')
  expect(svg).toContain('$284')
})

test('a value an earlier version stored is declined', async ($, on) => {
  on('state.get', () => ({ value: { value: { shape: 'cost-state-1', value: STORED }, version: 1 } }))

  const ui = await $.ui.mount({ ...PANE, surface: 'desktop' })

  expect((await ui.find({ type: 'Text', text: /Reading session transcripts/ }))).toBeTruthy()
  await ui.unmount()
})
