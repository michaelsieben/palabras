export type ChartRect = { bucket: number; series: number; x: number; y: number; width: number; height: number }

function check(values: readonly (readonly number[])[], width: number, height: number) {
  if (![width, height, ...values.flat()].every(value => Number.isFinite(value) && value >= 0)) throw new RangeError('Invalid chart dimensions or values')
}

// Common linear scale, zero baseline. No minimum-height exaggeration for small values.
export function stackedColumns(values: readonly (readonly number[])[], width: number, height: number): ChartRect[] {
  check(values, width, height)
  const maximum = Math.max(0, ...values.map(bucket => bucket.reduce((sum, value) => sum + value, 0)))
  if (!maximum || !width || !height) return []
  const slot = width / values.length
  return values.flatMap((bucket, index) => {
    let bottom = height
    return bucket.flatMap((value, series) => {
      const barHeight = value / maximum * height
      bottom -= barHeight
      return value ? [{ bucket: index, series, x: index * slot + slot * .15, y: bottom, width: slot * .7, height: barHeight }] : []
    })
  })
}

export function stackedBar(values: readonly number[], width: number, height: number): ChartRect[] {
  check([values], width, height)
  const total = values.reduce((sum, value) => sum + value, 0)
  if (!total || !width || !height) return []
  let left = 0
  return values.flatMap((value, series) => {
    const barWidth = value / total * width
    const x = left
    left += barWidth
    return value ? [{ bucket: 0, series, x, y: 0, width: barWidth, height }] : []
  })
}
