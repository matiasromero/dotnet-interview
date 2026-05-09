# Documentation for External Todo API

<a name="documentation-for-api-endpoints"></a>
## Documentation for API Endpoints

All URIs are relative to *http://localhost*

| Class | Method | HTTP request | Description |
|------------ | ------------- | ------------- | -------------|
| *TodoItemApi* | [**deleteTodoItem**](Apis/TodoItemApi.md#deletetodoitem) | **DELETE** /todolists/{todolistId}/todoitems/{todoitemId} | Delete a TodoItem |
*TodoItemApi* | [**updateTodoItem**](Apis/TodoItemApi.md#updatetodoitem) | **PATCH** /todolists/{todolistId}/todoitems/{todoitemId} | Update a TodoItem |
| *TodoListApi* | [**createTodoList**](Apis/TodoListApi.md#createtodolist) | **POST** /todolists | Create a new TodoList with items |
*TodoListApi* | [**deleteTodoList**](Apis/TodoListApi.md#deletetodolist) | **DELETE** /todolists/{todolistId} | Delete a TodoList and its items |
*TodoListApi* | [**listTodoLists**](Apis/TodoListApi.md#listtodolists) | **GET** /todolists | Fetch all TodoLists and their items |
*TodoListApi* | [**updateTodoList**](Apis/TodoListApi.md#updatetodolist) | **PATCH** /todolists/{todolistId} | Update a TodoList |


<a name="documentation-for-models"></a>
## Documentation for Models

 - [CreateTodoItemBody](./Models/CreateTodoItemBody.md)
 - [CreateTodoListBody](./Models/CreateTodoListBody.md)
 - [TodoItem](./Models/TodoItem.md)
 - [TodoList](./Models/TodoList.md)
 - [UpdateTodoItemBody](./Models/UpdateTodoItemBody.md)
 - [UpdateTodoListBody](./Models/UpdateTodoListBody.md)


<a name="documentation-for-authorization"></a>
## Documentation for Authorization

All endpoints do not require authorization.
