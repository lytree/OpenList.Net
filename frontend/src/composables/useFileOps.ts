import { ref } from "vue"
import { ElMessage } from "element-plus"
import { fsCopy, fsMkdir, fsMove, fsRemove, fsRename } from "~/api/fs"
import { pathJoin } from "~/utils/path"
import { t } from "~/i18n"
import type { Obj } from "~/api/types"
import type { useFileList } from "./useFileList"

/**
 * 文件操作集（新建/重命名/删除/移动/复制）。
 *
 * 约定：
 * - 全部走 `files.refresh()`（force=true）落盘后端目录缓存，列表立刻可见；
 * - 失败统一 ElMessage.error 展示后端 message（`Resp` 信封已在 client 层归一）；
 * - 选择态由本 composable 持有，表格 `@selection-change` 写入；
 * - 权限由调用方用 `files.canWrite` 控制显隐，这里只做最后兜底（不可写直接拒绝）。
 */
export function useFileOps(files: ReturnType<typeof useFileList>) {
  /** 表格多选（type=selection）。 */
  const selected = ref<Obj[]>([])
  /** 任一操作进行中：批量按钮统一禁用，防重复提交。 */
  const busy = ref(false)

  /** 统一执行器：包装请求、报错、成功提示与刷新。 */
  async function run(action: () => Promise<{ code: number; message: string }>, successText: string) {
    if (!files.canWrite.value) {
      ElMessage.warning("read-only")
      return false
    }
    busy.value = true
    try {
      const resp = await action()
      if (resp.code !== 200) {
        ElMessage.error(resp.message || "error")
        return false
      }
      ElMessage.success(successText)
      await files.refresh()
      selected.value = []
      return true
    } finally {
      busy.value = false
    }
  }

  async function mkdir(name: string) {
    const trimmed = name.trim()
    if (!trimmed) {
      ElMessage.warning(t("ops.name_required"))
      return false
    }
    return run(
      () => fsMkdir(pathJoin(files.path.value, trimmed)),
      t("ops.created"),
    )
  }

  async function rename(obj: Obj, newName: string) {
    const trimmed = newName.trim()
    if (!trimmed) {
      ElMessage.warning(t("ops.name_required"))
      return false
    }
    if (trimmed === obj.name) {
      ElMessage.info(t("ops.same_name"))
      return false
    }
    return run(
      () =>
        fsRename(
          pathJoin(files.path.value, obj.name),
          trimmed,
          false,
        ),
      t("ops.renamed"),
    )
  }

  async function remove(targets: Obj[]) {
    if (targets.length === 0) return false
    return run(
      () => fsRemove(files.path.value, targets.map((o) => o.name)),
      t("ops.deleted"),
    )
  }

  async function move(targets: Obj[], dstDir: string) {
    if (targets.length === 0) return false
    return run(
      () =>
        fsMove(
          files.path.value,
          dstDir,
          targets.map((o) => o.name),
        ),
      t("ops.moved"),
    )
  }

  async function copy(targets: Obj[], dstDir: string) {
    if (targets.length === 0) return false
    return run(
      () =>
        fsCopy(
          files.path.value,
          dstDir,
          targets.map((o) => o.name),
        ),
      t("ops.copied"),
    )
  }

  return { selected, busy, mkdir, rename, remove, move, copy }
}
