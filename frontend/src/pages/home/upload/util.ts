import type { UploadFileProps } from "./types"
import type { WorkerMessage } from "./hash-worker"
import HashWorker from "./hash-worker?worker&inline"

/**
 * 上传工具函数（对应旧前端 util.ts）。
 *
 * - traverseFileTree：拖拽整个目录时递归收集文件。注意 readEntries 每次
 *   最多返回 100 条（Chrome 行为），必须循环读到空数组为止。
 * - File2Upload：File 对象 -> 列表行数据。
 * - asyncPool：有界并发执行器（同时最多 N 个上传在跑）。
 * - calculateHash：worker 里算 md5/sha1/sha256 供秒传。
 */

export const traverseFileTree = async (entry: FileSystemEntry): Promise<File[]> => {
  const res: File[] = []

  const internalProcess = async (entry: FileSystemEntry, path: string) => {
    await new Promise<void>((resolve, reject) => {
      const errorCallback: ErrorCallback = (e) => {
        console.error(e)
        reject(e)
      }
      if (entry.isFile) {
        ;(entry as FileSystemFileEntry).file((file) => {
          // 把目录前缀并进文件名：上传时目标路径 = 当前目录 + path + name
          const newFile = new File([file], path + file.name, {
            type: file.type,
          })
          res.push(newFile)
          resolve()
        }, errorCallback)
      } else if (entry.isDirectory) {
        const dirReader = (entry as FileSystemDirectoryEntry).createReader()
        const readEntries = () => {
          dirReader.readEntries(async (entries) => {
            for (let i = 0; i < entries.length; i++) {
              await internalProcess(entries[i], path + entry.name + "/")
            }
            if (entries.length > 0) {
              readEntries()
            } else {
              resolve()
            }
          }, errorCallback)
        }
        readEntries()
      }
    })
  }

  await internalProcess(entry, "")
  return res
}

export const File2Upload = (file: File): UploadFileProps => {
  const name = file.name.replace(/\\/g, "/")
  return {
    name,
    path: name,
    size: file.size,
    progress: 0,
    speed: 0,
    status: "pending",
  }
}

/** 有界并发池：并发执行 mapper，异常被吞进返回值（由调用方决定如何上报）。 */
export async function* asyncPool<T, R>(
  concurrency: number,
  items: T[],
  mapper: (item: T) => Promise<R>,
): AsyncGenerator<R> {
  const queue = [...items]
  const workers = Array.from({ length: Math.min(concurrency, queue.length) }, async () => {
    const results: R[] = []
    for (;;) {
      const item = queue.shift()
      if (item === undefined) return results
      try {
        results.push(await mapper(item))
      } catch (e) {
        // mapper 的异常由调用方在自己的逻辑里处理（runOne 已有 try/catch）；
        // 池本身吞掉异常继续跑，避免一个失败拖垮其余任务
        console.error("asyncPool task failed:", e)
      }
    }
  })
  for (const group of await Promise.all(workers)) {
    for (const r of group) yield r
  }
}

export const calculateHash = async (
  file: File,
  onProgress?: (progress: number) => void,
): Promise<{ md5: string; sha1: string; sha256: string }> => {
  return new Promise((resolve, reject) => {
    const worker = new HashWorker()

    const terminate = (fn: () => void) => {
      worker.terminate()
      fn()
    }

    worker.postMessage({ file })

    worker.onmessage = (e: MessageEvent<WorkerMessage>) => {
      const data = e.data
      switch (data.type) {
        case "progress":
          onProgress?.(data.progress)
          break
        case "result":
          terminate(() => resolve(data.hash))
          break
        case "error":
          terminate(() => reject(new Error(data.error)))
          break
      }
    }

    worker.onerror = (e) => terminate(() => reject(new Error(e.message || "hash worker error")))
  })
}
