#pragma once

#include <string>
#include <thread>
#include <atomic>
#include <iostream>

#define CPPHTTPLIB_OPENSSL_SUPPORT
#undef CPPHTTPLIB_OPENSSL_SUPPORT
#include "httplib.h"

// Public-safe local HTTP utility used by the UI for metadata/workspace tasks.
// Intentionally excludes execution, injection, process, memory, and VM behavior.
class HttpServer {
public:
    static constexpr int PORT = 9753;
    static constexpr const char* HOST = "127.0.0.1";

    static void Start() {
        if (sRunning) return;

        sServer = new httplib::Server();
        sServer->Get("/ping", [](const httplib::Request&, httplib::Response& res) {
            res.set_content("pong", "text/plain");
        });

        sServer->Get("/about", [](const httplib::Request&, httplib::Response& res) {
            res.set_content("Aureo public core: runtime internals are closed-source", "text/plain");
        });

        sRunning = true;
        sServerThread = std::thread([]() {
            std::cout << "[HTTP] Public server listening on " << HOST << ":" << PORT << "\n";
            sServer->listen(HOST, PORT);
        });
        sServerThread.detach();
    }

    static void Stop() {
        if (!sRunning || !sServer) return;
        sServer->stop();
        sRunning = false;
    }

    static bool IsRunning() {
        return sRunning;
    }

private:
    static inline httplib::Server* sServer = nullptr;
    static inline std::thread sServerThread;
    static inline std::atomic<bool> sRunning{false};
};
