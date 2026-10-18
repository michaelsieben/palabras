import test from 'node:test'
import assert from 'node:assert/strict'
import { stackedColumns, stackedBar } from '../src/learn/svgCharts.ts'

const near = (actual, expected) => assert.ok(Math.abs(actual - expected) < 1e-9, `${actual} != ${expected}`)

test('columns share daily-total scale, stack from zero, preserve order and 30% gap', () => {
  assert.deepEqual(stackedColumns([[3, 1], [1, 1]], 200, 80), [
    { bucket: 0, series: 0, x: 15, y: 20, width: 70, height: 60 },
    { bucket: 0, series: 1, x: 15, y: 0, width: 70, height: 20 },
    { bucket: 1, series: 0, x: 115, y: 60, width: 70, height: 20 },
    { bucket: 1, series: 1, x: 115, y: 40, width: 70, height: 20 },
  ])
})

test('zero data, empty buckets and zero dimensions yield no rectangles, not NaN', () => {
  for (const values of [[], [[0, 0]], [[0], [0]]]) assert.deepEqual(stackedColumns(values, 360, 160), [])
  for (const values of [[], [0], [0, 0, 0]]) assert.deepEqual(stackedBar(values, 360, 32), [])
  assert.deepEqual(stackedColumns([[2, 1]], 0, 80), [])
  assert.deepEqual(stackedColumns([[2, 1]], 100, 0), [])
  assert.deepEqual(stackedBar([2, 1], 0, 32), [])
  assert.deepEqual(stackedBar([2, 1], 100, 0), [])
})

test('single day fills 70% of width and exact full scale; zero segments keep series index', () => {
  assert.deepEqual(stackedColumns([[0, 4]], 100, 80), [
    { bucket: 0, series: 1, x: 15, y: 0, width: 70, height: 80 },
  ])
  assert.deepEqual(stackedBar([0, 4, 0], 100, 32), [
    { bucket: 0, series: 1, x: 0, y: 0, width: 100, height: 32 },
  ])
})

test('stage bar widths are proportions of total, not tallest stage; segments abut', () => {
  assert.deepEqual(stackedBar([2, 0, 3, 5], 200, 32), [
    { bucket: 0, series: 0, x: 0, y: 0, width: 40, height: 32 },
    { bucket: 0, series: 2, x: 40, y: 0, width: 60, height: 32 },
    { bucket: 0, series: 3, x: 100, y: 0, width: 100, height: 32 },
  ])
})

test('outlier does not exaggerate small values or use separate series scales', () => {
  const rects = stackedColumns([[1, 1], [999999, 1]], 200, 100)
  near(rects[0].height, .0001)
  near(rects[1].height, .0001)
  near(rects[2].height, 99.9999)
  near(rects[3].height, .0001)
  near(rects[3].y, 0)
  const bar = stackedBar([1, 999999], 100, 32)
  near(bar[0].width, .0001)
  near(bar[1].x + bar[1].width, 100)
})

test('90 dense days fit narrow viewport with positive widths and no clipping', () => {
  const rects = stackedColumns(Array.from({ length: 90 }, (_, index) => [index + 1, 1]), 248, 160)
  assert.equal(rects.length, 180)
  for (const rect of rects) {
    assert.ok(rect.width > 0 && rect.height > 0)
    assert.ok(rect.x >= 0 && rect.x + rect.width <= 248)
    assert.ok(rect.y >= -1e-9 && rect.y + rect.height <= 160 + 1e-9)
  }
  near(rects.at(-1).x + rects.at(-1).width, 248 - 248 / 90 * .15)
})

test('invalid inputs are rejected, not converted into misleading coordinates', () => {
  for (const value of [-1, NaN, Infinity]) {
    assert.throws(() => stackedColumns([[value]], 100, 100), RangeError)
    assert.throws(() => stackedBar([value], 100, 32), RangeError)
    assert.throws(() => stackedColumns([[1]], value, 100), RangeError)
    assert.throws(() => stackedBar([1], 100, value), RangeError)
  }
})
