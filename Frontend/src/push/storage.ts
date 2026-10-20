export const deviceDatabase = 'palabras-push-device'
export const deviceLock = 'palabras-push-device'
export function createPushStore<T extends { id: string }>(name: string, factory: IDBFactory = indexedDB) {
  let opening: Promise<IDBDatabase> | undefined
  function open() {
    return opening ??= new Promise<IDBDatabase>((resolve, reject) => {
      const request = factory.open(name, 1)
      request.onupgradeneeded = () => request.result.createObjectStore('rows', { keyPath: 'id' })
      request.onerror = request.onblocked = () => { opening = undefined; reject(request.error ?? new Error('Push storage blocked')) }
      request.onsuccess = () => {
        const db = request.result
        db.onversionchange = () => { db.close(); opening = undefined }
        resolve(db)
      }
    })
  }
  async function transaction<R>(mode: IDBTransactionMode, work: (store: IDBObjectStore) => IDBRequest<R>): Promise<R> {
    const db = await open()
    return new Promise((resolve, reject) => {
      const tx = db.transaction('rows', mode)
      const request = work(tx.objectStore('rows'))
      tx.oncomplete = () => resolve(request.result)
      tx.onerror = tx.onabort = () => reject(tx.error ?? new Error('Push storage failed'))
    })
  }
  return {
    list: () => transaction('readonly', s => s.getAll()) as Promise<T[]>,
    put: (row: T) => transaction('readwrite', s => s.put(row)).then(() => undefined),
    remove: (id: string) => transaction('readwrite', s => s.delete(id)).then(() => undefined),
  }
}
let flagStore: ReturnType<typeof createPushStore<{ id: string; optedOut: boolean }>> | undefined
const flags = () => flagStore ??= createPushStore<{ id: string; optedOut: boolean }>(deviceDatabase)
export const readOptOut = async () => (await flags().list()).some(r => r.id === 'opt-out' && r.optedOut)
export const writeOptOut = (optedOut: boolean) => flags().put({ id: 'opt-out', optedOut })
