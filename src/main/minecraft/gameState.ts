import { emit } from '../core/bus'
import type { GameState } from '../../shared/types'

let state: GameState = { state: 'idle' }

export function emitGameState(next: GameState): void {
  state = next
  emit('game:state', next as unknown as Record<string, unknown>)
}

export function getGameState(): GameState {
  return state
}
