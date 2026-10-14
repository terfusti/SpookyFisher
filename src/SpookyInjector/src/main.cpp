
#include <iostream>
#include <Windows.h>
#include <TlHelp32.h>

DWORD GetProcId(const char* name)
{
	DWORD procID{};
	HANDLE snap{ CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) };

	if (snap != INVALID_HANDLE_VALUE)
	{
		PROCESSENTRY32 procEntry{};
		procEntry.dwSize = sizeof(procEntry);

		if (Process32First(snap, &procEntry))
		{
			do
			{
				if (!_stricmp(procEntry.szExeFile, name))
				{
					procID = procEntry.th32ProcessID;
					break;
				}
			} while (Process32Next(snap, &procEntry));
		}
	}
	CloseHandle(snap);
	return procID;
}


int main()
{
	const char* dllPath{ "C:\\Users\\thorl\\source\\repos\\SpookyFisher\\bin\\Win32\\Debug\\SpookyFisher.dll" };
	const char* procName{ "Terraria.exe" };
	DWORD procID{};

	while (!procID)
	{
		procID = GetProcId(procName);
		Sleep(30);
	}

	HANDLE proc{ OpenProcess(PROCESS_ALL_ACCESS, 0, procID) };

	if (proc && proc != INVALID_HANDLE_VALUE)
	{
		void* loc{ VirtualAllocEx(proc, 0, MAX_PATH, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE) };

		WriteProcessMemory(proc, loc, dllPath, strlen(dllPath) + 1, 0);

		HANDLE thread{ CreateRemoteThread(proc, 0, 0, (LPTHREAD_START_ROUTINE)LoadLibraryA, loc, 0, 0) };

		if (thread)
		{
			CloseHandle(thread);
		}
	}

	if (proc)
	{
		CloseHandle(proc);
	}
	return 0;
}