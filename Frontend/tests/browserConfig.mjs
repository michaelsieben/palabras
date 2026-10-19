const portText = process.env.PALABRAS_TEST_PORT ?? '4173'
export const testPort = Number(portText)
if (!/^\d+$/.test(portText) || !Number.isInteger(testPort) || testPort < 1 || testPort > 65535) {
  throw new Error('PALABRAS_TEST_PORT must be an integer between 1 and 65535')
}
export const testHost = '127.0.0.1'
export const testBaseUrl = `http://${testHost}:${testPort}`
