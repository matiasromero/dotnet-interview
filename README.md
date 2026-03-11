# dotnet-interview / TodoApi

[![Open in Coder](https://dev.crunchloop.io/open-in-coder.svg)](https://dev.crunchloop.io/templates/fly-containers/workspace?param.Git%20Repository=git@github.com:crunchloop/dotnet-interview.git)

This is a simple Todo List API built in .NET 8. This project is currently being used for .NET full-stack candidates.

## Getting Started

```bash
git clone --recurse-submodules git@github.com:crunchloop/dotnet-interview.git
cd dotnet-interview
```

If you already cloned without `--recurse-submodules`, run:

```bash
git submodule update --init --recursive
```

## Database

The project comes with a devcontainer that provisions a SQL Server database. If you are not going to use the devcontainer, make sure to provision a SQL Server database and
update the connection string.

## Build

To build the application:

`dotnet build`

## Run the API

To run the TodoApi in your local environment:

`dotnet run --project TodoApi`

## Test

To run tests:

`dotnet test`

## Frontend Challenge

This repo includes the [react-interview](https://github.com/crunchloop/react-interview) project as a git submodule in the `react-interview/` directory.

To run the frontend:

```bash
cd react-interview
npm install
npm run dev
```

The React app will be available at http://localhost:5173.

Check integration tests at: (https://github.com/crunchloop/interview-tests)

## Contact

- Martín Fernández (mfernandez@crunchloop.io)

## About Crunchloop

![crunchloop](https://crunchloop.io/logo-blue.png)

We strongly believe in giving back :rocket:. Let's work together [`Get in touch`](https://crunchloop.io/contact).
