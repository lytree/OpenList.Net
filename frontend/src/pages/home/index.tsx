import { defineComponent, computed, nextTick, onUnmounted, ref, watch } from "vue"
import {
  ElAlert,
  ElBreadcrumb,
  ElBreadcrumbItem,
  ElButton,
  ElButtonGroup,
  ElCard,
  ElDropdown,
  ElDropdownItem,
  ElDropdownMenu,
  ElEmpty,
  ElIcon,
  ElInput,
  ElMessage,
  ElMessageBox,
  ElOption,
  ElSelect,
  ElSpace,
  ElTag,
  ElTooltip,
} from "element-plus"
import { VexTable, VexColumn } from "~/components/table"
import {
  ArrowDown,
  ArrowLeft,
  ArrowRight,
  DocumentCopy,
  Expand,
  Folder,
  FolderAdd,
  Fold,
  MoreFilled,
  Refresh,
  Search,
  SortDown,
  SortUp,
  Upload,
} from "@element-plus/icons-vue"
import { useFileList, type SortKey } from "~/composables/useFileList"
import { useFileOps } from "~/composables/useFileOps"
import { useShortcuts } from "~/composables/useShortcuts"
import { useViewLayout, VIEW_MODES, type ViewMode } from "~/composables/useViewLayout"
import FolderPickerDialog from "~/components/FolderPickerDialog"
import FolderTree from "~/components/FolderTree"
import FileContextMenu, { type ContextMenuItem } from "~/components/FileContextMenu"
import FileGridView from "~/pages/home/GridView"
import FileListRows from "~/pages/home/ListRows"
import { kindColor, kindIcon } from "~/pages/home/kind"
import { rowActionsFor, type RowAction } from "~/pages/home/rowActions"
import { useIsPhone } from "~/composables/useIsPhone"
import FilePager from "~/pages/home/Pager"
import ToolbarDialogs, { type DialogKey } from "~/pages/home/toolbar/ToolbarDialogs"
import { isArchiveName } from "~/utils/archive"
import { isTorrentName, parseTorrent, torrentMagnet } from "~/utils/torrent"
import { onEvent } from "~/utils/bus"
import ReadmeCard from "~/pages/home/ReadmeCard"
import UploadPanel from "~/pages/home/upload/UploadPanel"
import { PreviewHost } from "~/pages/home/previews"
import { humanSize, formatTime } from "~/utils/format"
import { downloadLink } from "~/utils/link"
import { pathJoin, pathSegments } from "~/utils/path"
import { t } from "~/i18n"
import type { Obj } from "~/api/types"

/** ElMessageBox 的取消是 reject，这里统一吞掉 cancel。 */
function dismissCancel(e: unknown) {
  if (e !== "cancel" && e !== "close") ElMessage.error(String((e as Error)?.message ?? e))
}

/**
 * 文件浏览器（M1 第二片）。
 *
 * 本片范围：导航/列表/排序/密码门 + 操作集（新建/重命名/移动/复制/删除、
 * 多选批量、行内操作菜单）。上传与打包下载按方案第 10 节顺序后续补。
 */
export default defineComponent({
  name: "HomePage",
  setup() {
    const files = useFileList()
    const ops = useFileOps(files)
    const passwordInput = ref("")
    const { mode, treeVisible } = useViewLayout()

    /**
     * 手机断点内列表模式换成卡片行：表格四列在 390px 下必然横向溢出。
     * 这是「换结构」而不是「改尺寸」，所以由 JS 决定而不是纯 CSS。
     */
    const isPhone = useIsPhone()

    /** FolderPicker 的用途：move / copy；null 表示关着。 */
    const pickerMode = ref<"move" | "copy" | null>(null)

    /** VexTable 实例：Ctrl+A 走 toggleAllSelection；切回列表视图时恢复勾选。 */
    const tableRef = ref<{
      toggleAllSelection: () => void
      toggleRowSelection: (row: Obj, selected?: boolean) => void
      clearSelection: () => void
    } | null>(null)

    /** 前端内存「剪贴板」：Ctrl+C/X 存放，Ctrl+V 粘贴到当前目录。 */
    const clipboard = ref<{ mode: "copy" | "move"; objs: Obj[] } | null>(null)

    /** 右键菜单：null 表示关着。 */
    const ctxMenu = ref<{ x: number; y: number; items: ContextMenuItem[] } | null>(null)

    function objPath(obj: Obj) {
      return pathJoin(files.path.value, obj.name)
    }

    function copyObjLink(obj: Obj) {
      return copyRawUrl(downloadLink(objPath(obj), obj.sign))
    }

    function openObj(obj: Obj) {
      // 目录进列表，文件进预览页（`?path=/a/b.txt` 由 useFileList 判定为文件态）。
      // 不再 window.open 直链：「打开」应该停在应用内，下载另有入口。
      void files.open(obj)
    }

    /** 行右键菜单：打开/下载/复制直链/分享/打包 + 写操作（按权限与文件类型显隐）。 */
    function rowMenuItems(obj: Obj): ContextMenuItem[] {
      const items: ContextMenuItem[] = [
        { key: "open", label: t("ops.open"), handler: () => openObj(obj) },
      ]
      if (!obj.is_dir) {
        items.push(
          {
            key: "download",
            label: t("home.toolbar.download"),
            handler: () => window.open(downloadLink(objPath(obj), obj.sign), "_blank", "noopener"),
          },
          { key: "copy_link", label: t("home.toolbar.copy_link"), handler: () => void copyObjLink(obj) },
          { key: "share", label: "分享", handler: () => openDialog("share", [obj]) },
          { key: "package", label: "打包下载", handler: () => openDialog("package-download", [obj]) },
        )
        if (isArchiveName(obj.name)) {
          items.push({ key: "decompress", label: "解压", handler: () => openDialog("decompress", [obj]) })
        }
      } else {
        items.push(
          { key: "share", label: "分享", handler: () => openDialog("share", [obj]) },
          { key: "package", label: "打包下载", handler: () => openDialog("package-download", [obj]) },
        )
      }
      if (writable.value) {
        items.push(
          { key: "rename", label: t("ops.rename"), divided: true, handler: () => void promptRename(obj) },
          { key: "move", label: t("ops.move_to"), handler: () => openPicker("move", [obj]) },
          { key: "copy", label: t("ops.copy_to"), handler: () => openPicker("copy", [obj]) },
        )
        if (obj.is_dir) {
          items.push({
            key: "recursive_move",
            label: "递归移动",
            handler: () => openDialog("recursive-move", [obj]),
          })
        }
        items.push({ key: "delete", label: t("ops.delete"), danger: true, handler: () => void confirmRemove([obj]) })
      }
      return items
    }

    /** 空白处右键菜单：新建/上传/全选/粘贴/刷新。 */
    function blankMenuItems(): ContextMenuItem[] {
      const items: ContextMenuItem[] = [
        { key: "refresh", label: t("global.refresh"), handler: () => void files.refresh() },
        { key: "select_all", label: t("home.toolbar.select_all"), handler: () => tableRef.value?.toggleAllSelection() },
      ]
      if (writable.value) {
        items.unshift(
          { key: "mkdir", label: t("ops.new_folder"), handler: () => void promptMkdir() },
          { key: "new_file", label: "新建文件", handler: () => openDialog("new-file") },
          { key: "upload", label: t("home.upload.upload_files"), handler: () => (uploadVisible.value = true) },
          { key: "offline_download", label: "离线下载", handler: () => openDialog("offline-download") },
          { key: "remove_empty_dir", label: "删除空目录", handler: () => openDialog("remove-empty-dir") },
        )
      }
      if (clipboard.value && writable.value) {
        items.splice(2, 0, {
          key: "paste",
          label: `${t("global.paste")} (${clipboard.value.objs.length})`,
          handler: () => void pasteClipboard(),
        })
      }
      return items
    }

    function openRowCtx(obj: Obj, e: MouseEvent) {
      e.preventDefault()
      e.stopPropagation()
      ctxMenu.value = { x: e.clientX, y: e.clientY, items: rowMenuItems(obj) }
    }

    function openBlankCtx(e: MouseEvent) {
      if (files.state.value !== "folder") return
      e.preventDefault()
      ctxMenu.value = { x: e.clientX, y: e.clientY, items: blankMenuItems() }
    }

    /** Ctrl+V：把剪贴板内容复制/移动到当前目录。 */
    async function pasteClipboard() {
      const cb = clipboard.value
      if (!cb || !writable.value || cb.objs.length === 0) return
      clipboard.value = null
      if (cb.mode === "move") await ops.move(cb.objs, files.path.value)
      else await ops.copy(cb.objs, files.path.value)
    }

    useShortcuts({
      onSelectAll: () => {
        if (files.state.value !== "folder") return
        // 列表交给 VexTable（保证勾选框同步）；网格与手机卡片行没有表格实例，直接写选择数组。
        if (mode.value === "list" && !isPhone.value) tableRef.value?.toggleAllSelection()
        else ops.selected.value = [...files.viewObjs.value]
      },
      onDelete: () => {
        if (writable.value && ops.selected.value.length > 0) void confirmRemove(ops.selected.value)
      },
      onRename: () => {
        if (writable.value && ops.selected.value.length === 1) void promptRename(ops.selected.value[0])
      },
      onCopy: () => {
        if (ops.selected.value.length > 0) clipboard.value = { mode: "copy", objs: [...ops.selected.value] }
      },
      onCut: () => {
        if (writable.value && ops.selected.value.length > 0) clipboard.value = { mode: "move", objs: [...ops.selected.value] }
      },
      onPaste: () => void pasteClipboard(),
      onEscape: () => {
        if (ops.selected.value.length > 0) {
          // 走表格的 clearSelection 让勾选框与状态同步；网格视图直接清数组。
          if (mode.value === "list") tableRef.value?.clearSelection()
          ops.selected.value = []
        } else clipboard.value = null
      },
    })

    /** 上传面板开关；关面板时若有成功上传则刷新列表。 */
    const uploadVisible = ref(false)
    const uploadedDuringSession = ref(false)
    function onUploadVisibleChange(v: boolean) {
      uploadVisible.value = v
      if (!v && uploadedDuringSession.value) {
        uploadedDuringSession.value = false
        void files.refresh()
      }
    }

    /** 工具栏对话框：同一时刻只开一个，`targets` 是发起时的选择快照。 */
    const activeDialog = ref<DialogKey | null>(null)
    const dialogTargets = ref<Obj[]>([])
    /** 预填给对话框的内容（目前只有离线下载的链接框用）。 */
    const dialogUrls = ref<string[]>([])

    function openDialog(key: DialogKey, targets: Obj[] = [], urls: string[] = []) {
      dialogTargets.value = [...targets]
      dialogUrls.value = [...urls]
      activeDialog.value = key
    }

    /**
     * 预览器（种子预览）里的「离线下载」：把磁力链接递过来，由页面打开的对话框负责
     * 选工具/策略。预览器自己不该复制一份离线下载表单。
     */
    const offOfflineDownload = onEvent("offline-download", ({ urls }) => {
      openDialog("offline-download", [], urls)
    })
    onUnmounted(offOfflineDownload)

    function closeDialog() {
      activeDialog.value = null
    }

    /** 写完数据的操作统一刷新列表（force 穿透后端目录缓存）。 */
    function onDialogDone() {
      activeDialog.value = null
      void files.refresh()
    }

    /**
     * 把 .torrent 拖进列表区 → 本地解析出磁力 → 直接开离线下载对话框。
     *
     * 为什么不走「先上传再点开预览再点离线下载」：种子的用途就是喂给下载器，
     * 多存一份到网盘反而是垃圾。解析在本地完成，连上传流量都省了。
     *
     * 混入非种子文件时**整批拒绝**并提示，而不是只处理种子 —— 用户拖一堆文件
     * 进来通常是想上传，悄悄吞掉其中一部分比明确拒绝更糟。
     */
    async function onDropFiles(event: DragEvent) {
      event.preventDefault()
      const dropped = Array.from(event.dataTransfer?.files ?? [])
      if (dropped.length === 0) return // 拖的是目录：浏览器不会给 files，交给默认行为
      if (!dropped.every((file) => isTorrentName(file.name))) {
        ElMessage.warning(t("home.preview.torrent_drop_only"))
        return
      }
      try {
        const magnets: string[] = []
        for (const file of dropped) {
          const bytes = new Uint8Array(await file.arrayBuffer())
          magnets.push(torrentMagnet(parseTorrent(bytes)))
        }
        openDialog("offline-download", [], magnets)
      } catch (e) {
        ElMessage.error(String((e as Error)?.message ?? e))
      }
    }

    /**
     * 选择态的唯一真源是 `ops.selected`（Obj[]）；网格视图按名字比对。
     * 两个视图写同一个数组，所以「列表里勾了几项 → 切网格继续勾」不会丢。
     */
    const selectedNames = computed(() => ops.selected.value.map((obj) => obj.name))

    function toggleSelect(obj: Obj, checked: boolean) {
      const rest = ops.selected.value.filter((item) => item.name !== obj.name)
      ops.selected.value = checked ? [...rest, obj] : rest
    }

    // 切回列表视图时按名字把已选行重新勾上（VexTable 不读外部状态）；
    // 同时把「图墙只看目录+图片」这个约束交给数据层，让分页口径跟着变。
    watch(
      () => mode.value,
      async (next) => {
        files.kindFilter.value = next === "image" ? "image" : "all"
        if (next !== "list") return
        await nextTick()
        const table = tableRef.value
        if (!table) return
        const names = new Set(selectedNames.value)
        files.viewObjs.value.forEach((row) => table.toggleRowSelection(row, names.has(row.name)))
      },
      { immediate: true },
    )

    const segments = computed(() => pathSegments(files.path.value, t("global.nav_home")))
    /** 目录本身为空（不是被过滤词筛成空）。 */
    const isEmpty = computed(
      () => files.state.value === "folder" && files.objs.value.length === 0,
    )
    /** 目录有内容，但当前过滤词一条都没命中 —— 空态文案要区分这两种情况。 */
    const noMatch = computed(
      () =>
        files.state.value === "folder" &&
        files.objs.value.length > 0 &&
        files.filtering.value &&
        files.filteredObjs.value.length === 0,
    )
    const writable = computed(() => files.canWrite.value)

    const sortOptions: { value: SortKey; label: string }[] = [
      { value: "name", label: t("global.name") },
      { value: "size", label: "大小" },
      { value: "modified", label: "修改时间" },
    ]

    const iconOf = (row: Obj) => kindIcon(row)

    async function submitPassword() {
      if (!passwordInput.value) return
      files.password.value = passwordInput.value
      await files.load(true)
    }

    async function copyRawUrl(url: string) {
      try {
        await navigator.clipboard.writeText(url)
        ElMessage.success(t("global.copied"))
      } catch {
        ElMessage.error(t("global.clipboard_denied"))
      }
    }

    async function promptMkdir() {
      try {
        const { value } = await ElMessageBox.prompt(t("ops.folder_name"), t("ops.new_folder"), {
          confirmButtonText: t("global.confirm"),
          cancelButtonText: t("global.cancel"),
        })
        await ops.mkdir(value ?? "")
      } catch (e) {
        dismissCancel(e)
      }
    }

    async function promptRename(obj: Obj) {
      try {
        const { value } = await ElMessageBox.prompt(t("ops.new_name"), t("ops.rename"), {
          inputValue: obj.name,
          confirmButtonText: t("global.confirm"),
          cancelButtonText: t("global.cancel"),
        })
        await ops.rename(obj, value ?? "")
      } catch (e) {
        dismissCancel(e)
      }
    }

    async function confirmRemove(targets: Obj[]) {
      try {
        await ElMessageBox.confirm(
          t("ops.delete_confirm", { count: targets.length }),
          t("ops.delete"),
          { type: "warning", confirmButtonText: t("global.confirm"), cancelButtonText: t("global.cancel") },
        )
        await ops.remove(targets)
      } catch (e) {
        dismissCancel(e)
      }
    }

    function openPicker(mode: "move" | "copy", targets: Obj[]) {
      if (targets.length === 0) return
      ops.selected.value = targets
      pickerMode.value = mode
    }

    async function onPickerChosen(dst: string) {
      const mode = pickerMode.value
      pickerMode.value = null
      if (mode === "move") await ops.move(ops.selected.value, dst)
      else if (mode === "copy") await ops.copy(ops.selected.value, dst)
    }

    /** 行内菜单动作统一入口：把该行变成当前选择再复用批量逻辑。 */
    /**
     * 行内「更多」菜单：桌面表格的 `VexColumn` 与手机卡片行都用它渲染，命令统一交给
     * `onRowCommand`。规则本身在 `rowActionsFor`（纯函数，可单测）。
     */
    function rowActions(obj: Obj): RowAction[] {
      return rowActionsFor(obj, { writable: writable.value })
    }

    async function onRowCommand(command: string, obj: Obj) {
      switch (command) {
        case "open":
          openObj(obj)
          break
        case "download":
          window.open(downloadLink(objPath(obj), obj.sign), "_blank", "noopener")
          break
        case "copy_link":
          await copyObjLink(obj)
          break
        case "rename":
          await promptRename(obj)
          break
        case "move":
          openPicker("move", [obj])
          break
        case "copy":
          openPicker("copy", [obj])
          break
        case "delete":
          await confirmRemove([obj])
          break
      }
    }

  return () => (
    <div
      class="ol-files"
      onContextmenu={openBlankCtx}
      onDragover={(event: DragEvent) => {
        // 必须 preventDefault 才允许 drop；只对「拖动文件」生效，不干扰选中文字
        if (event.dataTransfer?.types.includes("Files")) event.preventDefault()
      }}
      onDrop={(event: DragEvent) => void onDropFiles(event)}
    >
      <div class="ol-home-split">
        {treeVisible.value && files.state.value !== "file" && (
          <aside class="ol-home-aside">
            <FolderTree
              current={files.path.value}
              password={files.password.value}
              onNavigate={(target: string) => void files.go(target)}
            />
          </aside>
        )}
        <div class="ol-home-main">
      <div class="ol-home-nav">
          <div class="ol-files-bar">
            <div class="ol-bar-left">
              <ElTooltip content={treeVisible.value ? "隐藏目录树" : "显示目录树"} placement="bottom">
                <ElButton
                  text={true}
                  size="small"
                  onClick={() => (treeVisible.value = !treeVisible.value)}
                >
                  <ElIcon>{treeVisible.value ? <Fold /> : <Expand />}</ElIcon>
                </ElButton>
              </ElTooltip>
              <ElBreadcrumb separator="/">
                {segments.value.map((seg, index) => (
                  <ElBreadcrumbItem>
                    <span
                      class={index === segments.value.length - 1 ? "ol-crumb ol-crumb-current" : "ol-crumb"}
                      onClick={() => {
                        if (index !== segments.value.length - 1) void files.go(seg.path)
                      }}
                    >
                      {seg.name}
                    </span>
                  </ElBreadcrumbItem>
                ))}
              </ElBreadcrumb>
            </div>

            <ElSpace size={8} wrap={true}>
              {files.state.value === "folder" && (
                <ElInput
                  v-model={files.keyword.value}
                  class="ol-files-search"
                  size="small"
                  placeholder="在当前目录筛选"
                  clearable={true}
                  v-slots={{ prefix: () => <ElIcon><Search /></ElIcon> }}
                />
              )}
              {/*
                视图切换用 ElButtonGroup 而不是 ElRadioGroup：
                Element Plus 2.9 的 ElRadioGroup 在 Vue 3.6-rc 运行时下渲染期抛
                `TypeError: e.scope.on is not a function`（整个组件树渲染中断）。
                已知的双 RC 风险（见 docs/unified-media-product-plan.md 第 9 节），
                按钮组语义等价、零兼容风险。
              */}
              {files.state.value === "folder" && (
                <ElButtonGroup size="small">
                  {VIEW_MODES.map((item) => (
                    <ElButton
                      key={item.value}
                      type={mode.value === item.value ? "primary" : "default"}
                      onClick={() => (mode.value = item.value)}
                    >
                      {item.label}
                    </ElButton>
                  ))}
                </ElButtonGroup>
              )}
              {writable.value && files.state.value === "folder" && (
                <ElButton size="small" type="primary" plain={true} onClick={promptMkdir}>
                  <ElIcon style={{ marginRight: "4px" }}>
                    <FolderAdd />
                  </ElIcon>
                  {t("ops.new_folder")}
                </ElButton>
              )}
              {writable.value && files.state.value === "folder" && (
                <ElButton size="small" type="primary" onClick={() => (uploadVisible.value = true)}>
                  <ElIcon style={{ marginRight: "4px" }}>
                    <Upload />
                  </ElIcon>
                  {t("home.upload.upload_files")}
                </ElButton>
              )}
              {writable.value && files.state.value === "folder" && (
                <ElDropdown
                  trigger="click"
                  onCommand={(cmd: string) => openDialog(cmd as DialogKey)}
                  v-slots={{
                    default: () => (
                      <ElButton size="small">
                        更多
                        <ElIcon class="el-icon--right">
                          <ArrowDown />
                        </ElIcon>
                      </ElButton>
                    ),
                    dropdown: () => (
                      <ElDropdownMenu>
                        <ElDropdownItem command="new-file">新建文件</ElDropdownItem>
                        <ElDropdownItem command="offline-download">离线下载</ElDropdownItem>
                        <ElDropdownItem command="remove-empty-dir" divided={true}>
                          删除空目录
                        </ElDropdownItem>
                      </ElDropdownMenu>
                    ),
                  }}
                />
              )}
              <ElButton
                size="small"
                disabled={files.path.value === "/"}
                onClick={() => void files.goUp()}
              >
                <ElIcon>
                  <ArrowLeft />
                </ElIcon>
              </ElButton>
              <ElButton size="small" onClick={() => void files.refresh()}>
                <ElIcon>
                  <Refresh />
                </ElIcon>
              </ElButton>
              <ElSelect
                modelValue={files.sortKey.value}
                class="ol-files-sort"
                size="small"
                onUpdate:modelValue={(v: SortKey) => (files.sortKey.value = v)}
              >
                {sortOptions.map((opt) => (
                  <ElOption key={opt.value} label={opt.label} value={opt.value} />
                ))}
              </ElSelect>
              <ElButton
                size="small"
                onClick={() =>
                  (files.sortOrder.value = files.sortOrder.value === "asc" ? "desc" : "asc")
                }
              >
                <ElIcon>{files.sortOrder.value === "asc" ? <SortUp /> : <SortDown />}</ElIcon>
              </ElButton>
              {/* 挂载点驱动信息（Local/S3/…）不再单列一个标签：参考前端这一行只有
                  面包屑 + 计数，多一个标签就会把控件挤到第二行。 */}
              {files.state.value === "folder" && (
                <ElTag size="small">
                  {files.pagedObjs.value.total === files.total.value
                    ? `共 ${files.total.value} 项`
                    : `显示 ${files.pagedObjs.value.total} / ${files.total.value} 项`}
                </ElTag>
              )}
              {clipboard.value && (
                <ElTag
                  size="small"
                  type={clipboard.value.mode === "move" ? "warning" : "success"}
                  style={{ cursor: "pointer" }}
                  onClick={() => void pasteClipboard()}
                >
                  {clipboard.value.mode === "move" ? "✂" : "⧉"} {clipboard.value.objs.length}
                </ElTag>
              )}
            </ElSpace>
          </div>

          {ops.selected.value.length > 0 && (
            <div class="ol-batch-bar">
              <span class="ol-batch-count">
                {t("ops.selected_count", { count: ops.selected.value.length })}
              </span>
              <ElSpace size={8}>
                <ElButton
                  size="small"
                  onClick={() => openDialog("package-download", ops.selected.value)}
                >
                  打包下载
                </ElButton>
                <ElButton size="small" onClick={() => openDialog("share", ops.selected.value)}>
                  分享
                </ElButton>
                {writable.value && (
                <>
                <ElButton
                  size="small"
                  disabled={ops.busy.value}
                  onClick={() => openPicker("move", ops.selected.value)}
                >
                  <ElIcon style={{ marginRight: "4px" }}>
                    <ArrowRight />
                  </ElIcon>
                  {t("ops.move_to")}
                </ElButton>
                <ElButton
                  size="small"
                  disabled={ops.busy.value}
                  onClick={() => openDialog("batch-rename", ops.selected.value)}
                >
                  重命名
                </ElButton>
                <ElButton
                  size="small"
                  disabled={ops.busy.value}
                  onClick={() => openPicker("copy", ops.selected.value)}
                >
                  <ElIcon style={{ marginRight: "4px" }}>
                    <DocumentCopy />
                  </ElIcon>
                  {t("ops.copy_to")}
                </ElButton>
                <ElButton
                  size="small"
                  type="danger"
                  plain={true}
                  disabled={ops.busy.value}
                  onClick={() => void confirmRemove(ops.selected.value)}
                >
                  {t("ops.delete")}
                </ElButton>
                </>
                )}
              </ElSpace>
            </div>
          )}
        </div>

        <ElCard
          shadow="never"
          bodyStyle={{
            // 列表/网格自己贴边（表格自带分割线，网格自带 padding）；空态与预览要留白。
            padding:
              files.state.value === "folder" && !isEmpty.value && !noMatch.value ? "0" : "20px",
          }}
        >
          {files.state.value === "loading" && (
            <div class="ol-files-center">
              <ElIcon class="is-loading" size={22}>
                <Refresh />
              </ElIcon>
            </div>
          )}

          {files.state.value === "need-password" && (
            <div class="ol-files-center">
              <ElIcon size={32}>
                <Folder />
              </ElIcon>
              <p style={{ color: "var(--ol-text-secondary)", fontSize: "13px" }}>
                {t("home.input_password")}
              </p>
              <ElSpace>
                <ElInput
                  v-model={passwordInput.value}
                  type="password"
                  showPassword={true}
                  style={{ width: "200px" }}
                />
                <ElButton type="primary" onClick={submitPassword}>
                  {t("global.confirm")}
                </ElButton>
              </ElSpace>
            </div>
          )}

          {files.state.value === "error" && (
            <ElAlert type="error" showIcon={true} title={files.errorMessage.value} closable={false} />
          )}

          {files.state.value === "file" && files.current.value && (
            <PreviewHost
              obj={files.current.value}
              path={files.path.value}
              provider={files.current.value.provider}
            />
          )}

          {isEmpty.value && <ElEmpty description="此目录为空" />}

          {noMatch.value && (
            <ElEmpty description={`没有匹配「${files.keyword.value}」的条目`} />
          )}

          {files.state.value === "folder" && !isEmpty.value && !noMatch.value && mode.value === "list" && !isPhone.value && (
            <VexTable
              ref={tableRef}
              data={files.viewObjs.value}
              rowKey="name"
              size="small"
              style={{ width: "100%" }}
              onSelection-change={(rows: Obj[]) => (ops.selected.value = rows)}
              onRow-contextmenu={(row: unknown, _column: unknown, event: MouseEvent) =>
                openRowCtx(row as Obj, event)
              }
            >
              {writable.value && <VexColumn type="selection" width={42} />}
              <VexColumn label={t("global.name")} minWidth={280}>
                {{
                  default: ({ row }) => {
                    const obj = row as unknown as Obj
                    const Icon = iconOf(obj)
                    return (
                      <span class="ol-file-name" onClick={() => void files.open(obj)}>
                        <ElIcon size={16} color={kindColor(obj)}>
                          <Icon />
                        </ElIcon>
                        <span>{obj.name}</span>
                      </span>
                    )
                  },
                }}
              </VexColumn>
              <VexColumn label="大小" width={130}>
                {{
                  default: ({ row }) => {
                    const obj = row as unknown as Obj
                    return obj.is_dir ? "-" : humanSize(obj.size)
                  },
                }}
              </VexColumn>
              <VexColumn label="修改时间" width={180}>
                {{
                  default: ({ row }) => formatTime((row as unknown as Obj).modified),
                }}
              </VexColumn>
              <VexColumn label={t("ops.actions")} width={64} align="center">
                {{
                  default: ({ row }) => {
                    const obj = row as unknown as Obj
                    return (
                      <ElDropdown
                        trigger="click"
                        onCommand={(command: string) => void onRowCommand(command, obj)}
                        v-slots={{
                          default: () => (
                            <ElButton text={true} size="small" onClick={(e: MouseEvent) => e.stopPropagation()}>
                              <ElIcon>
                                <MoreFilled />
                              </ElIcon>
                            </ElButton>
                          ),
                          dropdown: () => (
                            <ElDropdownMenu>
                              {rowActions(obj).map((item) => (
                                <ElDropdownItem
                                  key={item.command}
                                  command={item.command}
                                  divided={item.divided}
                                  class={item.danger ? "is-danger" : undefined}
                                >
                                  {item.label}
                                </ElDropdownItem>
                              ))}
                            </ElDropdownMenu>
                          ),
                        }}
                      />
                    )
                  },
                }}
              </VexColumn>
            </VexTable>
          )}

          {files.state.value === "folder" && !isEmpty.value && !noMatch.value && mode.value === "list" && isPhone.value && (
            <FileListRows
              objs={files.viewObjs.value}
              selected={selectedNames.value}
              selectable={writable.value}
              actions={rowActions}
              onOpen={(obj: Obj) => openObj(obj)}
              onToggle={(obj: Obj, checked: boolean) => toggleSelect(obj, checked)}
              onContextmenu={(obj: Obj, ev: MouseEvent) => openRowCtx(obj, ev)}
              onAction={(command: string, obj: Obj) => void onRowCommand(command, obj)}
            />
          )}

          {files.state.value === "folder" && !isEmpty.value && !noMatch.value && mode.value !== "list" && (
            <FileGridView
              objs={files.viewObjs.value}
              mode={mode.value === "image" ? "image" : "grid"}
              selected={selectedNames.value}
              path={files.path.value}
              selectable={writable.value}
              onOpen={(obj: Obj) => openObj(obj)}
              onToggle={(obj: Obj, checked: boolean) => toggleSelect(obj, checked)}
              onContextmenu={(obj: Obj, ev: MouseEvent) => openRowCtx(obj, ev)}
            />
          )}

          {files.state.value === "folder" && files.pagedObjs.value.pages > 1 && (
            <FilePager
              page={files.page.value}
              pageSize={files.pageSize.value}
              total={files.pagedObjs.value.total}
              pages={files.pagedObjs.value.pages}
              onUpdate:page={(p: number) => (files.page.value = p)}
              onUpdate:pageSize={(s: number) => (files.pageSize.value = s)}
            />
          )}
        </ElCard>

        {files.state.value === "folder" && (
          <ReadmeCard
            objs={files.objs.value}
            readme={files.readme.value}
            path={files.path.value}
          />
        )}
        </div>
        </div>

        <FolderPickerDialog
          visible={pickerMode.value !== null}
          title={pickerMode.value === "copy" ? t("ops.copy_to") : t("ops.move_to")}
          initialDir={files.path.value}
          sourceDir={files.path.value}
          onUpdate:visible={(v: boolean) => {
            if (!v) pickerMode.value = null
          }}
          onChosen={(dst: string) => void onPickerChosen(dst)}
        />

        <UploadPanel
          visible={uploadVisible.value}
          dir={files.path.value}
          password={files.password.value}
          directUploadTools={files.directUploadTools.value}
          onUpdate:visible={onUploadVisibleChange}
          onUploaded={() => (uploadedDuringSession.value = true)}
        />

        <ToolbarDialogs
          active={activeDialog.value}
          dir={files.path.value}
          password={files.password.value}
          targets={dialogTargets.value}
          urls={dialogUrls.value}
          onClose={closeDialog}
          onDone={onDialogDone}
        />

        <FileContextMenu
          visible={ctxMenu.value !== null}
          x={ctxMenu.value?.x ?? 0}
          y={ctxMenu.value?.y ?? 0}
          items={ctxMenu.value?.items ?? []}
          onUpdate:visible={(v: boolean) => {
            if (!v) ctxMenu.value = null
          }}
        />
      </div>
    )
  },
})
