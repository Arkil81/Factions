import type { CentralBankDto, FactionDto, MonetaryEventDto } from '../types/world'

function formatCredits(value: number) {
  return new Intl.NumberFormat('en-US').format(value)
}

function formatPercent(value: number) {
  return `${Math.round(value * 100)}%`
}

function gapTone(gap: number) {
  if (gap > 0) return '#2ecc71'
  if (gap < 0) return '#e67e22'
  return 'var(--text-1)'
}

function formatSignedCredits(value: number) {
  return `${value > 0 ? '+' : ''}${formatCredits(value)}`
}

function formatEventLabel(event: MonetaryEventDto) {
  const faction = event.factionTag ? ` ${event.factionTag}` : ''
  return `${event.type}${faction}`
}

export default function BankOverview({
  bank,
  factions,
}: {
  bank: CentralBankDto | null
  factions: FactionDto[]
}) {
  const ranked = [...factions].sort((a, b) => {
    if (b.debt !== a.debt) return b.debt - a.debt
    return a.treasury - b.treasury
  })

  if (!bank) {
    return <div className="bank-view-empty">Waiting for central-bank snapshot...</div>
  }

  const richest = [...factions].sort((a, b) => b.treasury - a.treasury).slice(0, 6)
  const indebted = ranked.filter(f => f.debt > 0).slice(0, 8)
  const bankEvents = [...bank.bankEvents].slice(-10).reverse()
  const debtEvents = [...bank.debtEvents].slice(-12).reverse()

  return (
    <div className="bank-view">
      <div className="bank-grid">
        <section className="bank-section">
          <div className="section-title">Monetary State</div>
          <div className="bank-metric-grid">
            <div className="bank-metric">
              <div className="bank-metric-label">Money Supply</div>
              <div className="bank-metric-value">{formatCredits(bank.currentMoneySupply)}</div>
            </div>
            <div className="bank-metric">
              <div className="bank-metric-label">Target Supply</div>
              <div className="bank-metric-value">{formatCredits(bank.targetMoneySupply)}</div>
            </div>
            <div className="bank-metric">
              <div className="bank-metric-label">Gap</div>
              <div className="bank-metric-value" style={{ color: gapTone(bank.moneySupplyGap) }}>
                {bank.moneySupplyGap > 0 ? '+' : ''}{formatCredits(bank.moneySupplyGap)}
              </div>
            </div>
            <div className="bank-metric">
              <div className="bank-metric-label">Collateral</div>
              <div className="bank-metric-value">{formatCredits(bank.worldCollateralValue)}</div>
            </div>
          </div>
          <div className="progress-wrap">
            <div className="progress-label">
              <span>Coverage</span>
              <span>{formatPercent(bank.targetMoneySupply > 0 ? bank.currentMoneySupply / bank.targetMoneySupply : 0)}</span>
            </div>
            <div className="progress-bar">
              <div
                className="progress-fill"
                style={{
                  width: `${Math.max(0, Math.min(100, bank.targetMoneySupply > 0 ? (bank.currentMoneySupply / bank.targetMoneySupply) * 100 : 0))}%`,
                  background: bank.moneySupplyGap >= 0 ? '#2ecc71' : '#e67e22',
                }}
              />
            </div>
          </div>
        </section>

        <section className="bank-section">
          <div className="section-title">Collateral Base</div>
          <div className="kv-list">
            <div className="kv-row"><span className="kv-label">Reserve Ratio</span><span className="kv-value">{formatPercent(bank.reserveRatio)}</span></div>
            <div className="kv-row"><span className="kv-label">Territory Nodes</span><span className="kv-value">{bank.territoryNodeCount}</span></div>
            <div className="kv-row"><span className="kv-label">Developed Nodes</span><span className="kv-value">{bank.foundedNodeCount}</span></div>
            <div className="kv-row"><span className="kv-label">Capability Levels</span><span className="kv-value">{bank.capabilityLevelCount}</span></div>
            <div className="kv-row">
              <span className="kv-label">Ships</span>
              <span className="kv-value">
                {bank.shipCount}
                {typeof bank.idleShipCount === 'number' && typeof bank.inFlightShipCount === 'number' && (
                  <span className="kv-sub"> ({bank.idleShipCount} idle · {bank.inFlightShipCount} in flight)</span>
                )}
              </span>
            </div>
          </div>
        </section>
      </div>

      <div className="bank-grid">
        <section className="bank-section">
          <div className="section-title">Treasury Leaders</div>
          <table className="data-table">
            <thead>
              <tr>
                <th>Faction</th>
                <th>Treasury</th>
                <th>Debt</th>
                <th>Worth</th>
              </tr>
            </thead>
            <tbody>
              {richest.map(f => (
                <tr key={f.tag}>
                  <td>{f.tag}</td>
                  <td>{formatCredits(f.treasury)}</td>
                  <td>{formatCredits(f.debt)}</td>
                  <td>{formatCredits(f.netWorth)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>

        <section className="bank-section">
          <div className="section-title">Debt Watch</div>
          {indebted.length === 0 ? (
            <div className="empty-msg">No factions currently carrying debt.</div>
          ) : (
            <table className="data-table">
              <thead>
                <tr>
                  <th>Faction</th>
                  <th>Debt</th>
                  <th>Support</th>
                  <th>Treasury</th>
                  <th>Assets</th>
                </tr>
              </thead>
              <tbody>
                {indebted.map(f => (
                  <tr key={f.tag}>
                    <td>{f.tag}</td>
                    <td>{formatCredits(f.debt)}</td>
                    <td>{formatCredits(f.supportDebt)}</td>
                    <td>{formatCredits(f.treasury)}</td>
                    <td>{formatCredits(f.assetValue)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </section>
      </div>

      <div className="bank-grid">
        <section className="bank-section">
          <div className="section-title">Bank Actions</div>
          {bankEvents.length === 0 ? (
            <div className="empty-msg">No central-bank actions recorded yet.</div>
          ) : (
            <div className="event-log">
              {bankEvents.map((event, index) => (
                <div key={`${event.utcNow}-${event.type}-${index}`} className="event-row">
                  <div className="event-head">
                    <span className="event-label">{formatEventLabel(event)}</span>
                    <span className={`event-amount ${event.amount >= 0 ? 'pos' : 'neg'}`}>
                      {formatSignedCredits(event.amount)}
                    </span>
                  </div>
                  <div className="event-meta">
                    <span>{new Date(event.utcNow).toLocaleTimeString()}</span>
                    <span>money {formatCredits(event.moneySupplyAfter)}</span>
                    <span>gap {formatSignedCredits(event.moneySupplyGapAfter)}</span>
                  </div>
                  <div className="event-message">{event.message}</div>
                </div>
              ))}
            </div>
          )}
        </section>

        <section className="bank-section">
          <div className="section-title">Debt Actions</div>
          {debtEvents.length === 0 ? (
            <div className="empty-msg">No debt actions recorded yet.</div>
          ) : (
            <div className="event-log">
              {debtEvents.map((event, index) => (
                <div key={`${event.utcNow}-${event.type}-${index}`} className="event-row">
                  <div className="event-head">
                    <span className="event-label">{formatEventLabel(event)}</span>
                    <span className={`event-amount ${event.amount >= 0 ? 'pos' : 'neg'}`}>
                      {formatSignedCredits(event.amount)}
                    </span>
                  </div>
                  <div className="event-meta">
                    <span>{new Date(event.utcNow).toLocaleTimeString()}</span>
                    <span>treasury {formatCredits(event.treasuryAfter)}</span>
                    <span>debt {formatCredits(event.debtAfter)}</span>
                  </div>
                  <div className="event-message">{event.message}</div>
                </div>
              ))}
            </div>
          )}
        </section>
      </div>
    </div>
  )
}
