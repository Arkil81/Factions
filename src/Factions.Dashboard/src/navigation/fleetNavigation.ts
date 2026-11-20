export type PanelKind = 'node' | 'fleet' | 'faction'

export interface PanelSelection {
  kind: PanelKind
  id: string
}

export interface FleetNavigation {
  selection: PanelSelection
  returnSelection: PanelSelection | null
}

export function openFleetDetail(fleetId: string, returnSelection: PanelSelection | null): FleetNavigation {
  return {
    selection: { kind: 'fleet', id: fleetId },
    returnSelection,
  }
}

export function returnFromFleetDetail(returnSelection: PanelSelection | null): PanelSelection | null {
  return returnSelection
}
