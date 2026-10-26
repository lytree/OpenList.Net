import { defineComponent, type PropType } from "vue"
import NewFileDialog from "./NewFileDialog"
import RemoveEmptyDirDialog from "./RemoveEmptyDirDialog"
import RecursiveMoveDialog from "./RecursiveMoveDialog"
import DecompressDialog from "./DecompressDialog"
import ShareDialog from "./ShareDialog"
import OfflineDownloadDialog from "./OfflineDownloadDialog"
import PackageDownloadDialog from "./PackageDownloadDialog"
import BatchRenameDialog from "./BatchRenameDialog"
import type { Obj } from "~/api/types"

/**
 * 首页工具栏对话框的**唯一渲染点**。
 *
 * 7 个操作各写一个 `xxxVisible` ref 会让页面 setup 长出 7 个互不相干的状态，
 * 还得在模板底部摆 7 个组件。这里收敛成「一个 active 键 + 一份 targets」：
 * 页面只负责 `openDialog("share", selected)`，谁该显示由本组件的 `active` 决定。
 *
 * `targets` 的语义随 active 变化：
 * - share / package / batch-rename：可以多选；
 * - recursive-move / decompress：调用方保证只传一项（多选时按钮本身就是禁用的）。
 */
export type DialogKey =
  | "new-file"
  | "remove-empty-dir"
  | "recursive-move"
  | "decompress"
  | "share"
  | "offline-download"
  | "package-download"
  | "batch-rename"

export default defineComponent({
  name: "ToolbarDialogs",
  props: {
    active: { type: String as PropType<DialogKey | null>, default: null },
    dir: { type: String, required: true },
    password: { type: String, default: "" },
    targets: { type: Array as PropType<Obj[]>, default: () => [] },
    /** 预填内容（离线下载的链接框）。只在 `offline-download` 下有意义。 */
    urls: { type: Array as PropType<string[]>, default: () => [] },
  },
  emits: {
    close: () => true,
    done: () => true,
  },
  setup(props, { emit }) {
    const is = (key: DialogKey) => props.active === key
    const close = () => emit("close")
    const done = () => emit("done")
    const first = () => props.targets[0] ?? null

    return () => (
      <>
        <NewFileDialog
          visible={is("new-file")}
          dir={props.dir}
          password={props.password}
          onUpdate:visible={close}
          onSaved={done}
        />
        <RemoveEmptyDirDialog
          visible={is("remove-empty-dir")}
          dir={props.dir}
          onUpdate:visible={close}
          onDone={done}
        />
        <RecursiveMoveDialog
          visible={is("recursive-move")}
          dir={props.dir}
          target={first()}
          onUpdate:visible={close}
          onDone={done}
        />
        <DecompressDialog
          visible={is("decompress")}
          dir={props.dir}
          target={first()}
          onUpdate:visible={close}
          onDone={done}
        />
        <ShareDialog
          visible={is("share")}
          dir={props.dir}
          targets={props.targets}
          onUpdate:visible={close}
        />
        <OfflineDownloadDialog
          visible={is("offline-download")}
          dir={props.dir}
          initialUrls={props.urls}
          onUpdate:visible={close}
          onDone={done}
        />
        <PackageDownloadDialog
          visible={is("package-download")}
          dir={props.dir}
          targets={props.targets}
          onUpdate:visible={close}
        />
        <BatchRenameDialog
          visible={is("batch-rename")}
          dir={props.dir}
          targets={props.targets}
          onUpdate:visible={close}
          onDone={done}
        />
      </>
    )
  },
})
