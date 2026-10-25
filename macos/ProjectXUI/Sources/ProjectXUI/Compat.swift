import SwiftUI

/// SwiftUI's `@State` property wrapper.
///
/// In the macOS 27 SDK, `@State` resolves to a compiler macro whose plugin ships only with Xcode. Naming
/// the property wrapper type through an alias skips the macro, so this package also builds with just
/// the Command Line Tools.
typealias ViewState<Value> = SwiftUI.State<Value>
