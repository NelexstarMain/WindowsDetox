export type ModelCost = {
  model: string
  label: string
  cost: number
  input: number
  output: number
  cacheWrite: number
  cacheRead: number
  messages: number
}

export type Hygiene = {
  projectSeconds: number
  projectThresholdSeconds: number
  globalSeconds: number
  globalThresholdSeconds: number
  queueEvents: number
  lastRunAt: string | null
  lastStatus: string | null
}

export type CostReport = {
  total: number
  today: number
  mainCost: number
  subagentCost: number
  sessions: number
  subagents: number
  days: { date: string; cost: number }[]
  models: ModelCost[]
  unpriced: string[]
  hygiene: Hygiene | null
  updatedAt: string
}

export type CostState = { report: CostReport | null; error: string | null; isLoading: boolean }

declare module 'claude-code' {
  interface PluginState {
    'cost-meter': { state: Shaped<CostState> }
  }
}
