// @vitest-environment happy-dom
//
// The Monitor's camera: when it fits everything again after the person moves the view.
import { describe, expect, it } from 'vitest'
import { MonitorScene, RefitAfterMs } from '../monitor/scene'

function scene() {
  const canvas = document.createElement('canvas')
  document.body.appendChild(canvas)
  const made = new MonitorScene(canvas, { fontPx: 12, fontFamily: 'sans-serif', reducedMotion: true })
  return { made, canvas }
}

/** The person moves the view with the wheel, then leaves it for `ms`. */
function moveThenWait(made: MonitorScene, canvas: HTMLCanvasElement, ms: number) {
  let now = 0
  made.step(now)
  canvas.dispatchEvent(new WheelEvent('wheel', { deltaY: -100, clientX: 10, clientY: 10, cancelable: true }))
  for (let waited = 0; waited <= ms; waited += 50) made.step((now += 50))
}

describe('the Monitor camera', () => {
  it('follows the graph until the person moves the view', () => {
    const { made } = scene()
    made.setAutoFit(false)

    expect(made.following).toBe(true)
  })

  it('fits everything again after the person leaves a moved view, by default', () => {
    const { made, canvas } = scene()

    moveThenWait(made, canvas, RefitAfterMs / 2)
    expect(made.following).toBe(false)

    moveThenWait(made, canvas, RefitAfterMs + 1000)
    expect(made.following).toBe(true)
  })

  it('keeps a moved view where the person put it with auto-fit off, until Fit', () => {
    const { made, canvas } = scene()
    made.setAutoFit(false)

    moveThenWait(made, canvas, RefitAfterMs * 3)
    expect(made.following).toBe(false)

    made.fit()
    expect(made.following).toBe(true)
  })
})
