// Separate account databases; API payloads never contain a client-supplied identity.
export function reviewDatabaseName(username: string) { return `palabras-review:${encodeURIComponent(username)}` }
export function createReviewStorage(name: string, factory: IDBFactory = indexedDB) {
  let opening: Promise<IDBDatabase> | undefined
  function open() {
    return opening ??= new Promise<IDBDatabase>((resolve, reject) => {
      const request = factory.open(name, 1)
      request.onupgradeneeded = () => {
        request.result.createObjectStore('actions', { keyPath: 'action.clientReviewId' })
        request.result.createObjectStore('cache')
      }
      request.onerror = request.onblocked = () => { opening = undefined; reject(request.error ?? new Error('Review storage blocked')) }
      request.onsuccess = () => {
        const db = request.result
        db.onversionchange = () => { db.close(); opening = undefined }
        resolve(db)
      }
    })
  }
  return async function transaction<T>(table: 'actions' | 'cache', mode: IDBTransactionMode, work: (store: IDBObjectStore) => IDBRequest<T> | void): Promise<T> {
    const db = await open()
    return new Promise((resolve, reject) => {
      const tx = db.transaction(table, mode)
      const request = work(tx.objectStore(table))
      tx.oncomplete = () => resolve(request?.result as T)
      tx.onerror = tx.onabort = () => reject(tx.error ?? new Error('Review storage failed'))
    })
  }
}
