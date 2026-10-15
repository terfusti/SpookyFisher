
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include <TlHelp32.h>
#include <filesystem>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <string>

struct HandleCloser
{
	void operator()(HANDLE handle) const noexcept
	{
		if (handle && handle != INVALID_HANDLE_VALUE) CloseHandle(handle);
	}
};
using Handle = std::unique_ptr<void, HandleCloser>;

static void Check(bool success, const char* operation)
{
	if (!success) throw std::runtime_error(std::string(operation) +
		" failed (Win32 " + std::to_string(GetLastError()) + ").");
}

static DWORD FindProcess(const wchar_t* name)
{
	Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
	Check(snapshot.get() != INVALID_HANDLE_VALUE, "Process snapshot");
	PROCESSENTRY32W entry{};
	entry.dwSize = sizeof(entry);
	Check(Process32FirstW(snapshot.get(), &entry) != FALSE, "Process enumeration");
	do
	{
		if (_wcsicmp(entry.szExeFile, name) == 0) return entry.th32ProcessID;
	} while (Process32NextW(snapshot.get(), &entry));
	return 0;
}

static MODULEENTRY32W FindModule(DWORD processId, const wchar_t* name)
{
	Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, processId));
	Check(snapshot.get() != INVALID_HANDLE_VALUE, "Module snapshot");
	MODULEENTRY32W entry{};
	entry.dwSize = sizeof(entry);
	Check(Module32FirstW(snapshot.get(), &entry) != FALSE, "Module enumeration");
	do
	{
		if (_wcsicmp(entry.szModule, name) == 0) return entry;
	} while (Module32NextW(snapshot.get(), &entry));
	return {};
}

// LoadLibraryW may be forwarded to KernelBase. Resolve its owner and RVA in the target.
static LPTHREAD_START_ROUTINE RemoteLoader(DWORD processId)
{
	const auto loader = GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW");
	Check(loader != nullptr, "Find LoadLibraryW");
	HMODULE owner = nullptr;
	Check(GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
		GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
		reinterpret_cast<LPCWSTR>(loader), &owner) != FALSE, "Find loader module");
	wchar_t ownerPath[32768]{};
	Check(GetModuleFileNameW(owner, ownerPath, 32768) != 0, "Loader module path");
	const auto ownerName = std::filesystem::path(ownerPath).filename().wstring();
	const auto remoteOwner = FindModule(processId, ownerName.c_str());
	if (remoteOwner.modBaseAddr)
	{
		const auto offset = reinterpret_cast<ULONG_PTR>(loader) - reinterpret_cast<ULONG_PTR>(owner);
		return reinterpret_cast<LPTHREAD_START_ROUTINE>(remoteOwner.modBaseAddr + offset);
	}
	throw std::runtime_error("LoadLibraryW's module was not found in Terraria.");
}

int wmain(int argc, wchar_t* argv[])
{
	if (argc > 2 || (argc == 2 && std::wstring(argv[1]) == L"--help"))
	{
		std::wcout << L"Usage: SpookyInjector.exe [path-to-SpookyFisher.dll]\n"
			L"Defaults to SpookyFisher.dll beside the injector. Match Terraria's architecture.\n";
		return argc > 2 ? 1 : 0;
	}
	try
	{
		wchar_t executable[32768]{};
		const DWORD length = GetModuleFileNameW(nullptr, executable, 32768);
		Check(length != 0 && length < 32768, "Injector path");
		const auto path = std::filesystem::absolute(argc == 2 ? std::filesystem::path(argv[1]) :
			std::filesystem::path(executable).parent_path() / L"SpookyFisher.dll");
		if (!std::filesystem::is_regular_file(path))
			throw std::runtime_error("Native DLL not found. Build the solution or provide its path.");

		std::wcout << L"Waiting for Terraria.exe...\n";
		DWORD processId = 0;
		while (!(processId = FindProcess(L"Terraria.exe"))) Sleep(100);
		Handle process(OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION |
			PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ, FALSE, processId));
		Check(process != nullptr, "Open Terraria");
		BOOL selfWow64 = FALSE, targetWow64 = FALSE;
		Check(IsWow64Process(GetCurrentProcess(), &selfWow64) != FALSE &&
			IsWow64Process(process.get(), &targetWow64) != FALSE, "Check architecture");
		if (selfWow64 != targetWow64)
			throw std::runtime_error("Injector and Terraria architectures differ. Build the matching x86/x64 configuration.");
		const auto dllName = path.filename().wstring();
		if (FindModule(processId, dllName.c_str()).modBaseAddr)
			throw std::runtime_error("DLL is already loaded. Detach first, or restart Terraria after an initialization failure.");
		const auto loader = RemoteLoader(processId);
		const std::wstring dllPath = path.wstring();
		const size_t bytes = (dllPath.size() + 1) * sizeof(wchar_t);
		void* remote = VirtualAllocEx(process.get(), nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
		Check(remote != nullptr, "Allocate DLL path");
		const auto freePath = [&process](void* memory) { VirtualFreeEx(process.get(), memory, 0, MEM_RELEASE); };
		std::unique_ptr<void, decltype(freePath)> allocation(remote, freePath);
		SIZE_T written = 0;
		Check(WriteProcessMemory(process.get(), remote, dllPath.c_str(), bytes, &written) != FALSE &&
			written == bytes, "Write DLL path");
		Handle thread(CreateRemoteThread(process.get(), nullptr, 0, loader, remote, 0, nullptr));
		Check(thread != nullptr, "Start DLL loader");
		if (WaitForSingleObject(thread.get(), INFINITE) != WAIT_OBJECT_0)
		{
			// Completion is unknown: keep the argument alive for the loader.
			allocation.release();
			Check(false, "Wait for DLL loader");
		}
		// A thread exit code truncates an HMODULE on x64; check the module list instead.
		if (!FindModule(processId, dllName.c_str()).modBaseAddr)
			throw std::runtime_error("LoadLibraryW failed in Terraria. Check DLL architecture and dependencies.");
		std::wcout << L"DLL loaded. Check bootstrap logs for managed initialization. F6: fishing; F7: detach.\n";
		return 0;
	}
	catch (const std::exception& error)
	{
		std::cerr << error.what() << '\n';
		return 1;
	}
}
