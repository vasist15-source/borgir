import SwiftUI

struct RootView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model

        Group {
            if let session = model.session {
                GameScreen(session: session)
                    .toolbar(.hidden, for: .windowToolbar)
            } else {
                LibraryView()
            }
        }
        .background {
            WindowReader { MainWindow.shared.attach($0) }
        }
        .task {
            model.start()
        }
        .alert(item: $model.alert) { alert in
            Alert(title: Text(alert.title), message: Text(alert.message))
        }
        .sheet(item: $model.controllerRequest) { request in
            ControllerRequestSheet(request: request)
        }
        .sheet(isPresented: Binding(get: { model.busyMessage != nil }, set: { if !$0 { model.busyMessage = nil } })) {
            VStack(spacing: 16) {
                ProgressView()
                    .controlSize(.large)
                Text(model.busyMessage ?? "")
                    .font(.headline)
            }
            .padding(32)
            .frame(minWidth: 320)
            .interactiveDismissDisabled()
        }
    }
}
